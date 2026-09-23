import Foundation
import Testing
@testable import MightyCore

struct CodexSubagentFlowTests {
    private func collab(_ id: String, tool: String, receivers: [String], prompt: String? = nil,
                        states: [String: [String: Any]] = [:], ended: Bool = true) -> [String: Any] {
        var item: [String: Any] = ["id": id, "type": "collab_tool_call", "tool": tool,
            "sender_thread_id": "root", "receiver_thread_ids": receivers, "agents_states": states,
            "status": ended ? "completed" : "in_progress"]
        if let prompt { item["prompt"] = prompt }
        return ["type": ended ? "item.completed" : "item.started", "item": item]
    }

    // Exercise the entire streamed event -> normal activity -> graph -> saved
    // history path, including UTF-8 boundaries and many concurrent agents.
    @Test func parallelAgentsAndFollowupSurviveStreamingAndProfileRoundTrip() throws {
        let workspace = Workspace(id: "workspace", name: "Fixture", path: "/tmp")
        var session = RunSession(id: "session", workspaceId: workspace.id, title: "Codex", provider: "codex")
        session.beginGraphRun(input: "모듈을 나눠 검토해 줘", id: "request", configuredModel: "default")
        var activities: [AgentActivity] = []
        var graphEvents: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "codex", log: { kind, text in
            session.recordGraph(RunEvent(sessionId: session.id, type: "log", entry: LogEntry(kind: kind, text: text, provider: "codex")))
        }, resume: { _ in }, activityNamespace: "parallel", activity: { activity in
            activities.append(activity)
            session.recordGraph(RunEvent(sessionId: session.id, type: "log",
                entry: LogEntry(id: activity.id, kind: "system", text: activity.summary, provider: "codex", activity: activity)))
        }, graph: { node in
            graphEvents.append(node)
            session.recordGraph(RunEvent(sessionId: session.id, type: "graph", graph: node))
        }, graphInput: "모듈을 나눠 검토해 줘")
        func send(_ value: [String: Any]) throws {
            let bytes = try JSONSerialization.data(withJSONObject: value) + Data([10])
            for start in stride(from: 0, to: bytes.count, by: 7) {
                parser.push(bytes.subdata(in: start..<min(bytes.count, start + 7)))
            }
        }
        try send(["type": "thread.started", "thread_id": "root"])
        let threads = (0..<32).map { "agent-\($0)" }
        for (index, thread) in threads.enumerated() {
            let prompt = "모듈 \(index) 검토"
            try send(collab("spawn-\(index)", tool: "spawn_agent", receivers: [], prompt: prompt, ended: false))
            try send(collab("spawn-\(index)", tool: "spawn_agent", receivers: [thread], prompt: prompt,
                            states: [thread: ["status": "running"]]))
        }
        #expect(session.mightyGraphRuns[0].agents.count == 32)
        #expect(session.mightyGraphRuns[0].agents.allSatisfy { $0.status == "running" && !$0.input.isEmpty })
        #expect(Set(session.mightyGraphRuns[0].agents.map(\.title)).count == 32)
        #expect(activities.filter { $0.kind == "agent" && $0.toolName == "spawn_agent" }.count >= 32)

        let completed = Dictionary(uniqueKeysWithValues: threads.map { ($0, ["status": "completed", "message": "\($0) 검토 완료"]) })
        try send(collab("wait-all", tool: "wait", receivers: threads, states: completed))
        let initial = session.mightyGraphRuns[0]
        #expect(initial.agents.allSatisfy { $0.status == "completed" })
        #expect(initial.resultEntries.isEmpty)
        let firstID = ExecutionGraphSupport.identifier("parallel", "codex-agent:agent-0")
        let oldSnapshot = try #require(graphEvents.last { $0.id == firstID })
        try send(collab("followup", tool: "send_input", receivers: ["agent-0"], prompt: "수정한 부분을 다시 검토",
                        states: ["agent-0": ["status": "running"]]))
        var first = try #require(session.mightyGraphRuns[0].agents.first { $0.id == firstID })
        #expect(first.status == "running")
        #expect(first.entries.contains { $0.kind == "user" && $0.text == "수정한 부분을 다시 검토" })
        #expect(first.entries.filter { $0.kind == "assistant" && $0.text == "agent-0 검토 완료" }.count == 1)
        #expect(session.mightyGraphRuns[0].agents.count == 32)
        // A delayed old snapshot must not settle the new assignment.
        session.recordGraph(RunEvent(sessionId: session.id, type: "graph", graph: oldSnapshot))
        first = try #require(session.mightyGraphRuns[0].agents.first { $0.id == firstID })
        #expect(first.status == "running")
        try send(collab("wait-followup", tool: "wait", receivers: ["agent-0"],
                        states: ["agent-0": ["status": "completed", "message": "재검토 완료"]]))
        try send(["type": "item.completed", "item": ["type": "agent_message", "id": "answer", "text": "전체 검토 완료"]])
        parser.finishActivities(stopped: false)
        parser.finishGraph(state: "completed")
        let run = try #require(session.mightyGraphRuns.first)
        #expect(run.settled)
        #expect(run.resultEntries.map(\.text) == ["전체 검토 완료"])
        #expect(run.resultEntries.allSatisfy { $0.provider == "codex" })
        #expect(run.agents.flatMap(\.entries).filter { $0.kind == "assistant" }.allSatisfy { $0.provider == "codex" })

        let snapshot = AppSnapshot(workspaces: [workspace], sessions: [session])
        let decoded = try JSONDecoder().decode(AppSnapshot.self, from: JSONEncoder().encode(snapshot))
        let restored = StateRepository.normalize(decoded, restoring: true)
        let restoredRun = try #require(restored.sessions.first?.mightyGraphRuns.first)
        #expect(restoredRun.agents.count == 32)
        #expect(restoredRun.agents.allSatisfy { $0.status == "completed" })
        #expect(restoredRun.resultEntries.first?.provider == "codex")
        #expect(restoredRun.resultEntries.first?.text == "전체 검토 완료")
        #expect(!parser.failed)
    }
}
