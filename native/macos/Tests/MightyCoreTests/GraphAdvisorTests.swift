import Foundation
import Testing
@testable import MightyCore

/// The advisor is Claude Code's server-side advisor tool (`claude --advisor <model>`, the `advisorModel` setting). In
/// the stream a consultation is a `server_tool_use` block named `advisor` in the caller's own message and, later, an
/// `advisor_tool_result` block whose content is encrypted (`advisor_redacted_result`), the advice as text
/// (`advisor_result`) or an error with its `error_code`; the turn's `result` names the advisor's own messages in
/// `usage.iterations[]` (`type: "advisor_message"`, with the model and the tokens).
///
/// The recording is native/contracts/fixtures/claude-stream-advisor.jsonl: Claude Code 2.1.322, Sonnet 5.5 consulting
/// an Opus 5.5 advisor once, its reply encrypted. The tracker keeps each consultation on the run's main block
/// (`GraphAdvisor`) and adds no tool block for it.
struct GraphAdvisorTests {
    private func send(_ value: [String: Any], to parser: CLIStreamParser) throws {
        var data = try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys, .withoutEscapingSlashes])
        data.append(10); parser.push(data)
    }
    /// A pane whose request runs through a parser that records its graph as the app does.
    private func pane(runID: String = "advised") -> (CLIStreamParser, () -> RunSession) {
        var session = RunSession(id: "pane", workspaceId: "space", title: "Claude", provider: "claude")
        session.recordGraph(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "request-one", kind: "user", text: "probe", provider: "claude")))
        let box = GraphAgentIdentityTests.Box(session)
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: runID,
                                     graph: { box.value.recordGraph(RunEvent(sessionId: "pane", type: "graph", graph: $0)) }, graphInput: "probe")
        return (parser, { box.value })
    }
    private func frame(_ content: [[String: Any]], id: String, parent: String? = nil, at time: String? = nil) -> [String: Any] {
        var value: [String: Any] = ["type": "assistant", "uuid": UUID().uuidString, "message": ["id": id, "model": "claude-sonnet-5-5", "content": content]]
        if let parent { value["parent_tool_use_id"] = parent }
        if let time { value["timestamp"] = time }
        return value
    }
    private func asked(_ call: String, message: String = "m1", parent: String? = nil, at time: String? = nil) -> [String: Any] {
        frame([["type": "server_tool_use", "id": call, "name": "advisor", "input": [String: Any]()]], id: message, parent: parent, at: time)
    }
    private func answered(_ call: String, _ content: [String: Any], message: String = "m1", parent: String? = nil, at time: String? = nil) -> [String: Any] {
        frame([["type": "advisor_tool_result", "tool_use_id": call, "content": content]], id: message, parent: parent, at: time)
    }
    private func used(_ tool: String, name: String = "Read", input: [String: Any] = [:], message: String = "m1", parent: String? = nil) -> [String: Any] {
        frame([["type": "tool_use", "id": tool, "name": name, "input": input]], id: message, parent: parent)
    }
    private func said(_ text: String, message: String = "m1", parent: String? = nil) -> [String: Any] {
        frame([["type": "text", "text": text]], id: message, parent: parent)
    }
    private func toolResult(_ tool: String, _ text: String, parent: String? = nil, structured: [String: Any]? = nil) -> [String: Any] {
        var value: [String: Any] = ["type": "user", "message": ["content": [["type": "tool_result", "tool_use_id": tool, "content": text, "is_error": false]]]]
        if let parent { value["parent_tool_use_id"] = parent }
        if let structured { value["tool_use_result"] = structured }
        return value
    }
    private func result(_ text: String, notice: Bool = false, iterations: [[String: Any]] = [], at time: String? = nil) -> [String: Any] {
        var event: [String: Any] = ["type": "result", "subtype": "success", "is_error": false, "result": text]
        if notice { event["origin"] = ["kind": "task-notification"] }
        if !iterations.isEmpty { event["usage"] = ["input_tokens": 4, "output_tokens": 9, "iterations": iterations] }
        if let time { event["timestamp"] = time }
        return event
    }
    private static let redacted: [String: Any] = ["type": "advisor_redacted_result", "encrypted_content": "encrypted"]
    private static func advisorMessage(input: Int, output: Int, model: String = "claude-opus-5-5") -> [String: Any] {
        ["type": "advisor_message", "model": model, "input_tokens": input, "output_tokens": output, "cache_read_input_tokens": 0, "cache_creation_input_tokens": 0]
    }

    // MARK: The recording

    @Test func theRecordedConsultationIsKeptOnTheRunAndMakesNoBlock() throws {
        let (parser, session) = pane(runID: "recorded")
        for line in try GraphAgentIdentityTests.fixture("claude-stream-advisor") { parser.push(line + Data([10])) }
        parser.flush(); parser.finishGraph(state: "completed")
        let runs = session().mightyGraphRuns
        #expect(runs.count == 1)
        let run = try #require(runs.first)
        let advisor = try #require(run.advisor)
        // One consultation, by the main session, from the frame that asked to the frame that carried the result.
        #expect(advisor.calls.count == 1 && advisor.count == 1 && advisor.total == nil)
        let call = try #require(advisor.calls.first)
        #expect(call.id == "srvtoolu_1")
        #expect(call.caller == nil)
        #expect(call.startedAt == "2026-11-10T18:16:51.658Z")
        #expect(call.endedAt == "2026-11-10T18:16:59.540Z")
        // The reply was encrypted: it went to Claude and there is no text.
        #expect(call.state == "done" && call.redacted == true && call.text == nil && call.errorCode == nil)
        // No file was changed before it, and the answer came right after it.
        #expect(call.beforeChange == true && call.followed == nil)
        // The turn's result names the advisor's model and what its own message cost.
        #expect(advisor.model == "claude-opus-5-5")
        #expect(advisor.usage == GraphTokenUsage(inputTokens: 34_217, outputTokens: 570))
        // The consultation is no block and no tool row; the request and its answer are as they would be without it.
        #expect(run.agents.isEmpty)
        #expect(!run.rootEntries.contains { $0.activity?.toolName == "advisor" })
        #expect(run.status == "completed" && run.finalOutput == "No")
        // The main session's own tokens do not count the advisor's.
        #expect(run.responseRecords?.map(\.model) == ["claude-sonnet-5-5"])
    }

    // MARK: Results

    @Test func aResultIsTheAdviceAsTextARefusalOrAnErrorWithItsCode() throws {
        let (parser, session) = pane()
        try send(asked("srvtoolu_a", at: "2026-11-10T10:00:00Z"), to: parser)
        #expect(session().mightyGraphRuns[0].advisor?.calls.map(\.state) == ["consulting"])
        try send(answered("srvtoolu_a", ["type": "advisor_result", "text": "Check the cache key first.", "stop_reason": "end_turn"], at: "2026-11-10T10:00:07Z"), to: parser)
        try send(asked("srvtoolu_b", message: "m2"), to: parser)
        try send(answered("srvtoolu_b", ["type": "advisor_result", "text": "", "stop_reason": "refusal"], message: "m2"), to: parser)
        try send(asked("srvtoolu_c", message: "m3"), to: parser)
        try send(answered("srvtoolu_c", ["type": "advisor_tool_result_error", "error_code": "max_uses_exceeded"], message: "m3"), to: parser)
        try send(asked("srvtoolu_d", message: "m4"), to: parser)
        try send(answered("srvtoolu_d", ["type": "advisor_tool_result_error"], message: "m4"), to: parser)
        try send(asked("srvtoolu_e", message: "m5"), to: parser)
        try send(answered("srvtoolu_e", ["type": "advisor_some_later_result", "payload": "x"], message: "m5"), to: parser)
        // A result for a call never seen, and a second result for one already settled, change nothing.
        try send(answered("srvtoolu_zz", Self.redacted), to: parser)
        try send(answered("srvtoolu_a", Self.redacted), to: parser)
        let calls = try #require(session().mightyGraphRuns[0].advisor?.calls)
        #expect(calls.map(\.id) == ["srvtoolu_a", "srvtoolu_b", "srvtoolu_c", "srvtoolu_d", "srvtoolu_e"])
        #expect(calls.map(\.state) == ["done", "declined", "error", "error", "done"])
        #expect(calls[0].text == "Check the cache key first." && calls[0].redacted == nil)
        #expect(calls[0].startedAt == "2026-11-10T10:00:00Z" && calls[0].endedAt == "2026-11-10T10:00:07Z")
        #expect(calls[1].text == nil)
        #expect(calls[2].errorCode == "max_uses_exceeded" && calls[3].errorCode == nil)
        // A shape this app does not know is a reply without text: nothing is made up for it.
        #expect(calls[4].text == nil && calls[4].redacted == nil && calls[4].errorCode == nil)
        // Asking again is going on: only the last one may be the last step.
        #expect(calls.map { $0.followed == true } == [true, true, true, true, false])
    }

    @Test func theAdviceIsBoundedAndOnlyAnAdvisorCallIsOne() throws {
        let (parser, session) = pane()
        try send(asked("srvtoolu_a"), to: parser)
        try send(answered("srvtoolu_a", ["type": "advisor_result", "text": String(repeating: "가", count: 4_000)]), to: parser)
        let text = try #require(session().mightyGraphRuns[0].advisor?.calls.first?.text)
        #expect(text.utf8.count <= GraphAdvisor.maximumTextBytes)
        // Another server tool is not the advisor; an id that is no identifier is not kept.
        try send(frame([["type": "server_tool_use", "id": "srvtoolu_web", "name": "web_search", "input": [String: Any]()]], id: "m2"), to: parser)
        try send(asked("bad id", message: "m3"), to: parser)
        #expect(session().mightyGraphRuns[0].advisor?.calls.map(\.id) == ["srvtoolu_a"])
    }

    // MARK: Where in the work

    @Test func aConsultationKnowsWhetherAFileWasChangedBeforeItAndWhetherWorkFollowed() throws {
        let (parser, session) = pane()
        func calls() -> [GraphAdvisorCall] { session().mightyGraphRuns[0].advisor?.calls ?? [] }
        // Before any file change.
        try send(used("toolu_read"), to: parser)
        try send(asked("srvtoolu_a", message: "m2"), to: parser)
        try send(answered("srvtoolu_a", Self.redacted, message: "m2"), to: parser)
        #expect(calls().map(\.beforeChange) == [true] && calls().map(\.followed) == [nil])
        // The main session goes on: a file-changing tool, then it asks again.
        try send(used("toolu_edit", name: "Edit", input: ["file_path": "/tmp/a.swift"], message: "m3"), to: parser)
        #expect(calls().map(\.followed) == [true])
        try send(asked("srvtoolu_b", message: "m4"), to: parser)
        try send(answered("srvtoolu_b", Self.redacted, message: "m4"), to: parser)
        #expect(calls().map(\.beforeChange) == [true, nil])
        // The answer follows the second one directly: it stays the last step.
        try send(said("Done.", message: "m4"), to: parser)
        try send(result("Done."), to: parser)
        #expect(calls().map(\.followed) == [true, nil])
        #expect(calls().map(\.state) == ["done", "done"])
    }

    @Test func oneFrameWithSeveralBlocksIsReadInItsOrder() throws {
        // A transcript line holds a whole message: the tool call after the consultation follows it, the one before does not.
        let (parser, session) = pane()
        try send(frame([["type": "tool_use", "id": "toolu_1", "name": "Write", "input": ["file_path": "/tmp/a"]],
                        ["type": "server_tool_use", "id": "srvtoolu_a", "name": "advisor", "input": [String: Any]()],
                        ["type": "advisor_tool_result", "tool_use_id": "srvtoolu_a", "content": Self.redacted],
                        ["type": "server_tool_use", "id": "srvtoolu_b", "name": "advisor", "input": [String: Any]()],
                        ["type": "advisor_tool_result", "tool_use_id": "srvtoolu_b", "content": Self.redacted]], id: "m1"), to: parser)
        let calls = try #require(session().mightyGraphRuns[0].advisor?.calls)
        #expect(calls.map(\.id) == ["srvtoolu_a", "srvtoolu_b"])
        #expect(calls.map(\.beforeChange) == [nil, nil])
        #expect(calls.map(\.followed) == [true, nil])
    }

    // MARK: Who asked

    @Test func aSubagentsConsultationNamesItsBlockAndIsKeptOnTheRequest() throws {
        let (parser, session) = pane()
        try send(frame([["type": "tool_use", "id": "toolu_agent", "name": "Agent", "input": ["description": "scout", "prompt": "Look.", "subagent_type": "Explore"]]], id: "m1"), to: parser)
        try send(asked("srvtoolu_main", message: "m2"), to: parser)
        try send(asked("srvtoolu_child", message: "c1", parent: "toolu_agent"), to: parser)
        try send(answered("srvtoolu_child", Self.redacted, message: "c1", parent: "toolu_agent"), to: parser)
        let run = session().mightyGraphRuns[0]
        let scout = try #require(run.agents.first { $0.isSubagent })
        let calls = try #require(run.advisor?.calls)
        #expect(calls.map(\.id) == ["srvtoolu_main", "srvtoolu_child"])
        #expect(calls.map(\.caller) == [nil, scout.id])
        #expect(calls.map(\.state) == ["consulting", "done"])
        // Each caller's work is its own: the sub-agent's tool call follows its consultation, not the main session's.
        try send(used("toolu_grep", name: "Grep", message: "c2", parent: "toolu_agent"), to: parser)
        #expect(session().mightyGraphRuns[0].advisor?.calls.map(\.followed) == [nil, true])
        // The sub-agent's block is as it would be without the advisor: no tool row for it.
        #expect(!(session().mightyGraphRuns[0].agents.first?.entries.contains { $0.activity?.toolName == "advisor" } ?? true))
        // The turn ends with the main session's consultation still out: it never got its result. The sub-agent's stands.
        try send(result("stopped early"), to: parser)
        #expect(session().mightyGraphRuns[0].advisor?.calls.map(\.state) == ["stopped", "done"])
    }

    @Test func aNoticeTurnsConsultationIsKeptOnItsOwnRun() throws {
        let (parser, session) = pane()
        try send(frame([["type": "tool_use", "id": "toolu_bg", "name": "Agent", "input": ["description": "count", "prompt": "Count.", "run_in_background": true]]], id: "m1"), to: parser)
        try send(toolResult("toolu_bg", "Async agent launched successfully.\nagentId: agent1", structured: ["isAsync": true, "status": "async_launched", "agentId": "agent1"]), to: parser)
        try send(asked("srvtoolu_request", message: "m2"), to: parser)
        try send(answered("srvtoolu_request", Self.redacted, message: "m2"), to: parser)
        try send(result("launched.", iterations: [Self.advisorMessage(input: 1_000, output: 50)]), to: parser)
        // The background agent ends: its notice opens a turn of its own, where the main session asks again.
        try send(["type": "system", "subtype": "task_notification", "task_id": "agent1", "tool_use_id": "toolu_bg", "status": "completed", "summary": "Done"], to: parser)
        try send(asked("srvtoolu_notice", message: "m3"), to: parser)
        try send(answered("srvtoolu_notice", Self.redacted, message: "m3"), to: parser)
        try send(result("done.", notice: true, iterations: [Self.advisorMessage(input: 2_000, output: 70)]), to: parser)
        parser.flush(); parser.finishGraph(state: "completed")
        let runs = session().mightyGraphRuns
        #expect(runs.map { $0.noticeRun == true } == [false, true])
        #expect(runs[0].advisor?.calls.map(\.id) == ["srvtoolu_request"])
        #expect(runs[1].advisor?.calls.map(\.id) == ["srvtoolu_notice"])
        // Each turn's result gives its own run what the advisor cost in it.
        #expect(runs[0].advisor?.usage == GraphTokenUsage(inputTokens: 1_000, outputTokens: 50))
        #expect(runs[1].advisor?.usage == GraphTokenUsage(inputTokens: 2_000, outputTokens: 70))
        #expect(runs[1].advisor?.model == "claude-opus-5-5")
        #expect(runs[1].finalOutput == "done.")
    }

    // MARK: Ends

    @Test func aConsultationStillOutWhenTheProcessEndsIsStopped() throws {
        let (parser, session) = pane()
        try send(frame([["type": "tool_use", "id": "toolu_agent", "name": "Agent", "input": ["description": "scout", "prompt": "Look."]]], id: "m1"), to: parser)
        try send(asked("srvtoolu_child", message: "c1", parent: "toolu_agent", at: "2026-11-10T10:00:00Z"), to: parser)
        parser.flush(); parser.finishGraph(state: "stopped")
        let call = try #require(session().mightyGraphRuns[0].advisor?.calls.first)
        #expect(call.state == "stopped" && call.endedAt != nil)
    }

    @Test func theTokensOfSeveralTurnsAddUpAndAResultWithoutAdvisorMessagesAddsNothing() throws {
        let (parser, session) = pane()
        try send(result("plain", iterations: [["type": "message", "input_tokens": 9, "output_tokens": 9]]), to: parser)
        #expect(session().mightyGraphRuns[0].advisor == nil)
        let (second, other) = pane()
        try send(result("advised", iterations: [Self.advisorMessage(input: 100, output: 10), ["type": "message", "input_tokens": 9, "output_tokens": 9],
                                                 Self.advisorMessage(input: 300, output: 30, model: "claude-opus-5-5")]), to: second)
        // The advisor's messages alone, though the stream showed no consultation of it (a transcript read from its middle).
        let advisor = try #require(other().mightyGraphRuns[0].advisor)
        #expect(advisor.usage == GraphTokenUsage(inputTokens: 400, outputTokens: 40))
        #expect(advisor.model == "claude-opus-5-5" && advisor.calls.isEmpty && advisor.count == 0)
    }

    // MARK: Bounds and saves

    @Test func onlyTheNewestConsultationsAreKeptAndTheCountSaysAll() throws {
        let (parser, session) = pane()
        for index in 0..<40 {
            try send(asked("srvtoolu_\(index)", message: "m\(index)"), to: parser)
            try send(answered("srvtoolu_\(index)", Self.redacted, message: "m\(index)"), to: parser)
        }
        let advisor = try #require(session().mightyGraphRuns[0].advisor)
        #expect(advisor.calls.count == GraphAdvisor.maximumCalls)
        #expect(advisor.calls.first?.id == "srvtoolu_8" && advisor.calls.last?.id == "srvtoolu_39")
        #expect(advisor.count == 40 && advisor.total == 40)
    }

    @Test func aSavedRecordIsBoundedAndARestoredConsultationStillOutIsStopped() throws {
        let time = "2026-11-10T10:00:00Z"
        let record = GraphAdvisor(model: "not a model", usage: GraphTokenUsage(inputTokens: 5),
                                  calls: [GraphAdvisorCall(id: "srvtoolu_a", startedAt: time, state: "consulting", redacted: false, beforeChange: false),
                                          GraphAdvisorCall(id: "srvtoolu_a", state: "done"),
                                          GraphAdvisorCall(id: "bad id", state: "done"),
                                          GraphAdvisorCall(id: "srvtoolu_b", caller: "not an id", state: "done"),
                                          GraphAdvisorCall(id: "srvtoolu_c", state: "thinking"),
                                          GraphAdvisorCall(id: "srvtoolu_d", caller: "graph-abc", startedAt: "yesterday", endedAt: time, state: "error", text: "",
                                                           errorCode: "overloaded\nplease", followed: true)])
        let kept = try #require(record.normalized())
        #expect(kept.model == nil)
        #expect(kept.calls.map(\.id) == ["srvtoolu_a", "srvtoolu_d"])
        #expect(kept.calls[0] == GraphAdvisorCall(id: "srvtoolu_a", startedAt: time, state: "consulting"))
        #expect(kept.calls[1].startedAt == nil && kept.calls[1].endedAt == time && kept.calls[1].text == nil)
        #expect(kept.calls[1].errorCode == "overloaded please" && kept.calls[1].followed == true && kept.calls[1].caller == "graph-abc")
        // Six were seen, two are kept.
        #expect(kept.total == 6 && kept.count == 6)
        #expect(record.normalized(restoring: true)?.calls.map(\.state) == ["stopped", "error"])
        #expect(GraphAdvisor().normalized() == nil)
        #expect(GraphAdvisor(calls: [GraphAdvisorCall(id: "bad id")]).normalized() == nil)

        // A run carries it through a save; a save from before the advisor has none; only a main block keeps one.
        var run = MightyGraphRun(id: "r1", input: "go", status: "running", provider: "claude", advisor: record, configuredAdvisor: "opus")
        let data = try JSONEncoder().encode(run)
        let decoded = try JSONDecoder().decode(MightyGraphRun.self, from: data)
        #expect(decoded.advisor == record && decoded.configuredAdvisor == "opus")
        var budget = 1_000_000
        let restored = try #require(MightyGraphSupport.normalized([run], restoring: true, budget: &budget).first)
        #expect(restored.advisor?.calls.map(\.state) == ["stopped", "error"] && restored.configuredAdvisor == "opus")
        run.configuredAdvisor = "not a model"
        #expect(MightyGraphSupport.normalized([run], restoring: false, budget: &budget).first?.configuredAdvisor == nil)
        let old = try JSONDecoder().decode(MightyGraphRun.self, from: Data(#"{"id":"r0","input":"go","status":"completed","rootEntries":[],"agents":[],"resultEntries":[]}"#.utf8))
        #expect(old.advisor == nil && old.configuredAdvisor == nil)
        let plain = try JSONEncoder().encode(MightyGraphRun(id: "r2", input: "go", status: "completed", provider: "claude"))
        #expect(!String(decoding: plain, as: UTF8.self).contains("dvisor"))
        let agent = ExecutionGraphNode(id: "graph-a", runId: "r1", parentId: "graph-m", kind: "agent", state: "running", title: "a", advisor: record)
        #expect(ExecutionGraphSupport.normalized(agent)?.advisor == nil)
        let main = ExecutionGraphNode(id: "graph-m", runId: "r1", kind: "main", state: "running", title: "Claude", advisor: record)
        #expect(ExecutionGraphSupport.normalized(main)?.advisor == kept)
        #expect(ExecutionGraphSupport.normalized(main, restoring: true)?.advisor?.calls.first?.state == "stopped")
    }

    @Test func aRequestRemembersTheAdvisorItWasStartedWithAndItsNoticeRunsDoToo() throws {
        var session = RunSession(id: "pane", workspaceId: "space", title: "Claude", provider: "claude")
        session.beginGraphRun(input: "go", id: "r1", configuredModel: "sonnet", configuredAdvisor: "opus")
        session.beginGraphRun(input: "again", id: "r2", configuredModel: "sonnet", configuredAdvisor: "not a model")
        session.beginGraphRun(input: "plain", id: "r3", configuredModel: "sonnet")
        #expect(session.graphRuns?.map(\.configuredAdvisor) == ["opus", nil, nil])
        // Another agent has no advisor.
        var codex = RunSession(id: "codex", workspaceId: "space", title: "Codex", provider: "codex")
        codex.beginGraphRun(input: "go", id: "c1", configuredModel: "default", configuredAdvisor: "opus")
        #expect(codex.graphRuns?.map(\.configuredAdvisor) == [nil])
    }
}
