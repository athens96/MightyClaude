import Foundation
import Testing
@testable import MightyCore

/// Claude Code 2.1.322 sends a heartbeat 30 s into a foreground Bash call (`tool_progress`, `heartbeat: true`) whose
/// `parent_tool_use_id` is that call. It is no sub-agent's message: no block comes of it, and the call's output never
/// becomes a sub-agent's answer. Blocks an earlier build made of it are dropped when the pane is read back.
///
/// The recording is native/contracts/fixtures/claude-stream-tool-heartbeat.jsonl: one foreground Bash call of 40 s, its
/// `task_started` and `task_notification` (`local_bash`, not backgrounded), the heartbeat, its result and the answer.
struct ToolHeartbeatTests {
    private func replay() throws -> [MightyGraphRun] {
        var session = RunSession(id: "pane", workspaceId: "space", title: "Claude", provider: "claude")
        session.recordGraph(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "request-one", kind: "user", text: "probe", provider: "claude")))
        let box = GraphAgentIdentityTests.Box(session)
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "recorded",
                                     graph: { box.value.recordGraph(RunEvent(sessionId: "pane", type: "graph", graph: $0)) }, graphInput: "probe")
        for line in try GraphAgentIdentityTests.fixture("claude-stream-tool-heartbeat") { parser.push(line + Data([10])) }
        parser.flush(); parser.finishGraph(state: "completed")
        return box.value.mightyGraphRuns
    }

    @Test func theRecordedHeartbeatMakesNoBlock() throws {
        let lines = try GraphAgentIdentityTests.fixture("claude-stream-tool-heartbeat")
        #expect(lines.contains { String(decoding: $0, as: UTF8.self).contains(#""type":"tool_progress""#) })
        let runs = try replay()
        let request = try #require(runs.first)
        #expect(request.agents.isEmpty)
        #expect(request.status == "completed")
    }

    @Test func aFrameUnderACallThatIsNoAgentCallMakesNoBlock() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "run", graph: { nodes.append($0) })
        func push(_ frame: [String: Any]) throws { parser.push(try JSONSerialization.data(withJSONObject: frame) + Data([10])) }
        try push(["type": "assistant", "message": ["id": "m1", "role": "assistant", "content": [["type": "tool_use", "id": "read-1", "name": "Read", "input": ["file_path": "/tmp/a"]]]]])
        try push(["type": "assistant", "parent_tool_use_id": "read-1", "message": ["id": "m2", "role": "assistant", "content": [["type": "text", "text": "x"]]]])
        try push(["type": "assistant", "message": ["id": "m3", "role": "assistant", "content": [["type": "tool_use", "id": "agent-1", "name": "Agent", "input": ["description": "Map the repo", "prompt": "Map it"]]]]])
        try push(["type": "assistant", "parent_tool_use_id": "agent-1", "message": ["id": "m4", "role": "assistant", "content": [["type": "text", "text": "mapping"]]]])
        parser.flush()
        #expect(!nodes.contains { $0.id == ExecutionGraphSupport.agentNodeID(runId: "run", toolUseId: "read-1") })
        #expect(nodes.contains { $0.id == ExecutionGraphSupport.agentNodeID(runId: "run", toolUseId: "agent-1") && $0.title == "Map the repo" })
    }

    @Test func aHeartbeatBlockAnEarlierBuildSavedIsDroppedOnRead() {
        let heartbeat = MightyGraphAgent(id: "graph-bash", title: "하위 에이전트", input: "", status: "completed",
                                         entries: [LogEntry(id: "graph-bash-answer", kind: "assistant", text: "Build complete!", provider: "claude")],
                                         figures: GraphAgentFigures(startedAt: "2026-11-11T15:17:41Z", endedAt: "2026-11-11T15:19:02.999Z"))
        // A sub-agent that has not got its title yet still has its prompt; one that has a title is never dropped.
        let real = MightyGraphAgent(id: "graph-agent", title: "하위 에이전트", input: "Map the repo", status: "running")
        let titled = MightyGraphAgent(id: "graph-named", title: "Map", input: "", status: "completed")
        let run = MightyGraphRun(id: "r", input: "요청", status: "completed", agents: [heartbeat, real, titled], provider: "claude")
        var budget = 10_000_000
        let read = MightyGraphSupport.normalized([run], restoring: true, budget: &budget)
        #expect(read.first?.agents.map(\.id) == ["graph-agent", "graph-named"])
        // Another agent's sub-agent may have no prompt and only its answer: it is never taken for one.
        var codex = run; codex.provider = "codex"
        var other = 10_000_000
        #expect(MightyGraphSupport.normalized([codex], restoring: true, budget: &other).first?.agents.count == 3)
    }

    @Test func savedStepsAreBoundedOnRead() {
        var run = MightyGraphRun(id: "r", input: "요청", status: "completed", provider: "claude")
        run.steps = (0..<150).map { FlowStep(key: "text:\($0)", content: "단계 \($0)", status: $0 == 0 ? "done?" : "completed", startedAt: "not a time") }
        var budget = 10_000_000
        let steps = MightyGraphSupport.normalized([run], restoring: true, budget: &budget).first?.steps ?? []
        #expect(steps.count == TodoProgress.maximumItems)
        #expect(steps[0].status == "pending" && steps.allSatisfy { $0.startedAt == nil })
    }
}
