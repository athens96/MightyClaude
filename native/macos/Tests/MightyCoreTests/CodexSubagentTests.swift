import Foundation
import Testing
@testable import MightyCore

struct CodexSubagentTests {
    private func send(_ value: [String: Any], _ parser: CLIStreamParser) throws {
        parser.push(try JSONSerialization.data(withJSONObject: value)); parser.push("\n")
    }
    private func call(_ id: String, tool: String, targets: [String] = [], prompt: String? = nil,
                      states: [String: [String: Any]] = [:], status: String = "completed", sender: String = "root") -> [String: Any] {
        var result: [String: Any] = ["id": id, "type": "collab_tool_call", "tool": tool, "sender_thread_id": sender,
                                     "receiver_thread_ids": targets, "agents_states": states, "status": status]
        if let prompt { result["prompt"] = prompt }; return result
    }
    private func event(_ item: [String: Any], _ type: String = "item.completed") -> [String: Any] { ["type": type, "item": item] }
    private func child(_ values: [ExecutionGraphNode], _ thread: String) throws -> ExecutionGraphNode {
        try #require(values.last { $0.id == ExecutionGraphSupport.identifier("run", "codex-agent:" + thread) })
    }

    @Test func ordinaryModeShowsStableAgentRowsTaskTargetsResultsAndDuration() throws {
        var rows: [AgentActivity] = []; var now: Double = 0
        let parser = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in }, activityNamespace: "run", activity: { rows.append($0) }, activityClock: { now })
        try send(event(call("spawn", tool: "spawn_agent", prompt: "Inspect authentication", status: "in_progress"), "item.started"), parser)
        now = 0.5
        try send(event(call("spawn", tool: "spawn_agent", targets: ["worker-1"], prompt: "Inspect authentication", states: ["worker-1": ["status": "running"]])), parser)
        #expect(rows.count == 2)
        #expect(rows[0].id == rows[1].id)
        #expect(rows.allSatisfy { $0.kind == "agent" && $0.toolName == "spawn_agent" })
        #expect(rows[1].state == "completed")
        #expect(rows[1].durationMs == 500)
        #expect(rows[1].summary.contains("Inspect authentication"))
        #expect(rows[1].summary.contains("worker-1"))
        try send(event(call("wait", tool: "wait", targets: ["worker-1"], states: ["worker-1": ["status": "completed", "message": "Auth verified"]])), parser)
        #expect(rows.last?.output?.contains("Auth verified") == true)
        #expect(rows.last?.kind == "agent")
        #expect(!parser.failed)
    }

    @Test func toolAcknowledgementsAndFailuresNeverClaimChildCompletionOrFailure() throws {
        var nodes: [ExecutionGraphNode] = []; var rows: [AgentActivity] = []
        let parser = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in }, activityNamespace: "run", activity: { rows.append($0) }, graph: { nodes.append($0) })
        try send(["type": "thread.started", "thread_id": "root"], parser)
        try send(event(call("spawn", tool: "spawn_agent", targets: ["worker"], prompt: "Audit API", states: ["worker": ["status": "running"]])), parser)
        #expect(try child(nodes, "worker").state == "running")
        #expect(try child(nodes, "worker").title.contains("Audit API"))
        for tool in ["wait", "send_input", "close_agent"] {
            try send(event(call("failed-" + tool, tool: tool, targets: ["worker"], prompt: "Rejected instruction", status: "failed")), parser)
            #expect(try child(nodes, "worker").state == "running")
            #expect(rows.last?.state == "error")
        }
        #expect(try child(nodes, "worker").entries.isEmpty)
        try send(event(call("bad-spawn", tool: "spawn_agent", status: "failed")), parser)
        #expect(Set(nodes.filter { $0.kind == "agent" }.map(\.id)).count == 1)
        try send(event(call("result", tool: "wait", targets: ["worker"], states: ["worker": ["status": "completed", "message": "Audit complete"]])), parser)
        #expect(try child(nodes, "worker").state == "completed")
        #expect(try child(nodes, "worker").output == "Audit complete")
        try send(event(call("close-done", tool: "close_agent", targets: ["worker"], states: ["worker": ["status": "shutdown"]])), parser)
        #expect(try child(nodes, "worker").state == "completed")
        #expect(!parser.failed)
    }

    @Test func successfulNewInputReopensSameAgentExactlyOncePreservingPriorAnswer() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in }, activityNamespace: "run", graph: { nodes.append($0) })
        try send(["type": "thread.started", "thread_id": "root"], parser)
        try send(event(call("spawn", tool: "spawn_agent", targets: ["worker"], prompt: "First task", states: ["worker": ["status": "running"]])), parser)
        try send(event(call("done", tool: "wait", targets: ["worker"], states: ["worker": ["status": "completed", "message": "First answer"]])), parser)
        try send(event(call("old-wait", tool: "wait", targets: ["worker"], status: "in_progress"), "item.started"), parser)
        let followup = event(call("followup", tool: "send_input", targets: ["worker"], prompt: "Second task", states: ["worker": ["status": "running"]]))
        try send(followup, parser)
        var latest = try child(nodes, "worker")
        #expect(latest.state == "running")
        #expect(latest.activityGeneration == 1)
        #expect(latest.output == nil)
        #expect(latest.input == "First task")
        #expect(latest.entries.map(\.text) == ["First answer", "Second task"])
        try send(event(call("old-wait", tool: "wait", targets: ["worker"], states: ["worker": ["status": "completed", "message": "Stale first answer"]])), parser)
        #expect(try child(nodes, "worker").state == "running")
        #expect(try child(nodes, "worker").output == nil)
        try send(event(call("done-again", tool: "wait", targets: ["worker"], states: ["worker": ["status": "completed", "message": "Second answer"]])), parser)
        try send(followup, parser)
        try send(event(call("followup", tool: "send_input", targets: ["worker"], status: "in_progress"), "item.started"), parser)
        latest = try child(nodes, "worker")
        #expect(latest.state == "completed")
        #expect(latest.activityGeneration == 1)
        #expect(latest.output == "Second answer")
        #expect(Set(nodes.filter { $0.kind == "agent" }.map(\.id)).count == 1)
    }

    @Test func concurrentAcceptedInputsAreBothPreservedWithoutApplyingStaleReturnedState() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in }, activityNamespace: "run", graph: { nodes.append($0) })
        try send(event(call("spawn", tool: "spawn_agent", targets: ["worker"], prompt: "Original task")), parser)
        for id in ["one", "two"] {
            try send(event(call(id, tool: "send_input", targets: ["worker"], prompt: id, status: "in_progress"), "item.started"), parser)
        }
        try send(event(call("one", tool: "send_input", targets: ["worker"], prompt: "one", states: ["worker": ["status": "running"]])), parser)
        try send(event(call("two", tool: "send_input", targets: ["worker"], prompt: "two", states: ["worker": ["status": "completed", "message": "Old state"]])), parser)
        let latest = try child(nodes, "worker")
        #expect(latest.activityGeneration == 2)
        #expect(latest.state == "running")
        #expect(latest.output == nil)
        #expect(latest.entries.filter { $0.kind == "user" }.map(\.text) == ["one", "two"])
    }

    @Test func shutdownAndMissingFinalAnswersRemainStoppedRatherThanSuccessful() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in }, activityNamespace: "run", graph: { nodes.append($0) })
        try send(event(call("spawn", tool: "spawn_agent", targets: ["closed", "unfinished"], states: ["closed": ["status": "running"], "unfinished": ["status": "running"]])), parser)
        try send(event(call("close", tool: "close_agent", targets: ["closed"], states: ["closed": ["status": "shutdown"]])), parser)
        #expect(try child(nodes, "closed").state == "stopped")
        parser.finishGraph(state: "completed")
        #expect(try child(nodes, "unfinished").state == "stopped")
        #expect(nodes.last?.state == "completed")
        #expect(Set(nodes.filter { $0.kind == "agent" }.map(\.id)).count == 2)
    }

    @Test func parentAliasesResolveOutOfOrderWithoutCyclesAndNestedRowsStayInChild() throws {
        var nodes: [ExecutionGraphNode] = []; var rows: [AgentActivity] = []
        let parser = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in }, activityNamespace: "run", activity: { rows.append($0) }, graph: { nodes.append($0) })
        try send(["type": "thread.started", "thread_id": "root"], parser)
        // Defensive compatibility: current exec is root-scoped, but sender IDs
        // allow an explicitly observed nested spawn to retain its real parent.
        try send(event(call("nested", tool: "spawn_agent", targets: ["nested-worker"], prompt: "Nested work", sender: "parent")), parser)
        #expect(rows.isEmpty)
        try send(event(call("parent", tool: "spawn_agent", targets: ["parent"], prompt: "Parent work")), parser)
        let parent = try child(nodes, "parent"); let nested = try child(nodes, "nested-worker")
        #expect(nested.parentId == parent.id)
        #expect(parent.entries.first?.activity?.toolName == "spawn_agent")
        try send(event(call("cycle", tool: "spawn_agent", targets: ["parent"], sender: "nested-worker")), parser)
        #expect(try child(nodes, "parent").parentId == ExecutionGraphSupport.mainNodeID(runId: "run"))
    }

    @Test func malformedIDsUnknownToolsAndUnsupportedAppServerItemsAreIgnoredAndStateIsBounded() throws {
        var nodes: [ExecutionGraphNode] = []; var rows: [AgentActivity] = []
        let parser = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in }, activityNamespace: "run", activity: { rows.append($0) }, graph: { nodes.append($0) })
        for tool in ["followup_task", "resume_agent", "list_agents", "unsupported"] {
            try send(event(call("unknown-" + tool, tool: tool, targets: ["ignored"])), parser)
        }
        try send(event(call("bad\nidentifier", tool: "spawn_agent", targets: ["ignored"])), parser)
        #expect(rows.isEmpty)
        #expect(nodes.count == 1)
        let targets = (0..<300).map { "worker-\($0)" }
        try send(event(call("many", tool: "spawn_agent", targets: targets, prompt: String(repeating: "🦀", count: 10_000))), parser)
        #expect(Set(nodes.map(\.id)).count <= ExecutionGraphSupport.maximumNodes)
        #expect(nodes.allSatisfy { ($0.input?.utf8.count ?? 0) <= ExecutionGraphSupport.maximumInputBytes && $0.title.utf8.count <= 160 })
        #expect(rows.last?.summary.utf8.count ?? 0 <= ActivitySupport.maximumSummaryBytes)
    }
}
