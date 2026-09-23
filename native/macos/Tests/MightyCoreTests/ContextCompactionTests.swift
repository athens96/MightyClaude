import Foundation
import Testing
@testable import MightyCore

struct ContextCompactionTests {
    private func send(_ value: [String: Any], to parser: CLIStreamParser) throws {
        var data = try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys, .withoutEscapingSlashes])
        data.append(10); parser.push(data)
    }

    @Test func summariesReadTheClaudeMetadata() {
        #expect(ContextCompaction.claudeSummary(["trigger": "auto", "pre_tokens": 84120, "post_tokens": 21300, "duration_ms": 12300]) == "자동 정리 · 84,120 → 21,300 토큰 · 12.3초")
        #expect(ContextCompaction.claudeSummary(["trigger": "manual", "pre_tokens": 5000, "messages_summarized": 40, "duration_ms": 800]) == "수동 정리 · 5,000 토큰에서 요약 · 메시지 40개 요약 · 800ms")
        #expect(ContextCompaction.claudeSummary(["trigger": "auto", "pre_tokens": 1000, "post_tokens": 400, "duration_ms": 3000]) == "자동 정리 · 1,000 → 400 토큰 · 3초")
        #expect(ContextCompaction.claudeSummary(nil) == "자동 정리")
        #expect(ContextCompaction.claudeSummary(["pre_tokens": -5, "post_tokens": "x"]) == "자동 정리")
        // Out-of-range numbers must not trap the parser; they are ignored.
        #expect(ContextCompaction.claudeSummary(["pre_tokens": 1e300, "duration_ms": Double(Int64.max)]) == "자동 정리")
        #expect(ContextCompaction.claudeSummary(["pre_tokens": 2147483647.0]) == "자동 정리 · 2,147,483,647 토큰에서 요약")
    }

    @Test func claudeCompactBoundaryBecomesACompletedBlockAndALogLine() throws {
        var nodes: [ExecutionGraphNode] = []
        var logs: [(String, String)] = []
        let parser = CLIStreamParser(provider: "claude", log: { logs.append(($0, $1)) }, resume: { _ in }, activityNamespace: "run-compact", graph: { nodes.append($0) }, graphInput: "Long task")
        let main = ExecutionGraphSupport.mainNodeID(runId: "run-compact")
        let metadata: [String: Any] = ["trigger": "auto", "pre_tokens": 84120, "post_tokens": 21300, "duration_ms": 12300]
        try send(["type": "system", "subtype": "compact_boundary", "uuid": "c1", "session_id": "main-session", "compact_metadata": metadata], to: parser)
        let block = try #require(nodes.last(where: { $0.kind == "compact" }))
        #expect(block.parentId == main); #expect(block.state == "completed"); #expect(block.title == "컨텍스트 정리")
        #expect(block.input == "자동 정리 · 84,120 → 21,300 토큰 · 12.3초")
        #expect(logs.contains { $0.0 == "system" && $0.1 == "컨텍스트 정리 · 자동 정리 · 84,120 → 21,300 토큰 · 12.3초" })
        // The same frame again is one block; a frame without an id still gets its own.
        try send(["type": "system", "subtype": "compact_boundary", "uuid": "c1", "compact_metadata": ["trigger": "auto", "pre_tokens": 1]], to: parser)
        try send(["type": "system", "subtype": "compact_boundary", "compact_metadata": ["trigger": "manual", "pre_tokens": 10]], to: parser)
        #expect(Set(nodes.filter { $0.kind == "compact" }.map(\.id)).count == 2)
        // A subagent compacting hangs under that agent and is not logged at the root.
        let spawn: [String: Any] = ["type": "tool_use", "id": "child", "name": "Agent", "input": ["description": "Inspect", "prompt": "Go"] as [String: Any]]
        let message: [String: Any] = ["id": "m1", "content": [spawn]]
        try send(["type": "assistant", "uuid": "m1", "session_id": "main-session", "message": message], to: parser)
        let before = logs.count
        try send(["type": "system", "subtype": "compact_boundary", "uuid": "c2", "parent_tool_use_id": "child", "compact_metadata": ["trigger": "auto", "pre_tokens": 300, "post_tokens": 100]], to: parser)
        let nested = try #require(nodes.last(where: { $0.kind == "compact" && $0.input?.contains("300") == true }))
        #expect(nested.parentId == ExecutionGraphSupport.agentNodeID(runId: "run-compact", toolUseId: "child"))
        #expect(logs.count == before)
        parser.finishGraph(state: "completed")
        #expect(nodes.last(where: { $0.id == block.id })?.state == "completed")
    }

    @Test func codexContextCompactionItemBecomesABlockWhenCompleted() throws {
        var nodes: [ExecutionGraphNode] = []
        var logs: [(String, String)] = []
        let parser = CLIStreamParser(provider: "codex", log: { logs.append(($0, $1)) }, resume: { _ in }, activityNamespace: "run-codex-compact", graph: { nodes.append($0) }, graphInput: "Long task")
        try send(["type": "thread.started", "thread_id": "thread-1"], to: parser)
        try send(["type": "item.started", "item": ["id": "cc1", "type": "context_compaction"]], to: parser)
        #expect(!nodes.contains { $0.kind == "compact" })
        try send(["type": "item.completed", "item": ["id": "cc1", "type": "context_compaction"]], to: parser)
        let block = try #require(nodes.last(where: { $0.kind == "compact" }))
        #expect(block.parentId == ExecutionGraphSupport.mainNodeID(runId: "run-codex-compact")); #expect(block.state == "completed")
        #expect(block.input == "Codex가 대화 맥락을 요약했습니다.")
        #expect(logs.contains { $0.0 == "system" && $0.1 == "컨텍스트 정리 · Codex가 대화 맥락을 요약했습니다." })
        try send(["type": "item.completed", "item": ["id": "cc1", "type": "context_compaction"]], to: parser)
        #expect(nodes.filter { $0.kind == "compact" }.count == 1)
    }

    @Test func sessionsKeepTheCompactKindThroughProjectionAndRestore() throws {
        var session = RunSession(workspaceId: "workspace", title: "Claude")
        session.beginGraphRun(input: "Refactor", id: "request-one", configuredModel: "default")
        let main = ExecutionGraphSupport.mainNodeID(runId: "process-one")
        session.recordGraph(RunEvent(sessionId: session.id, type: "graph", graph: ExecutionGraphNode(id: main, runId: "process-one", kind: "main", state: "running", title: "Claude")))
        let block = ExecutionGraphNode(id: "process-one-compact", runId: "process-one", parentId: main, kind: "compact", state: "completed", title: "컨텍스트 정리", input: "자동 정리 · 1,000 → 400 토큰")
        session.recordGraph(RunEvent(sessionId: session.id, type: "graph", graph: block))
        let agent = try #require(session.mightyGraphRuns[0].agents.first)
        #expect(agent.isCompact && !agent.isSteer && !agent.isTask); #expect(agent.parentID == nil); #expect(agent.input == "자동 정리 · 1,000 → 400 토큰")
        let restored = try JSONDecoder().decode(RunSession.self, from: try JSONEncoder().encode(session))
        var budget = 1_000_000
        let normalized = MightyGraphSupport.normalized(restored.graphRuns ?? [], restoring: true, budget: &budget)
        #expect(normalized[0].agents[0].isCompact)
    }
}
