import Foundation
import Testing
@testable import MightyCore

private final class GraphEventRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var items: [RunEvent] = []
    func append(_ item: RunEvent) { lock.lock(); items.append(item); lock.unlock() }
    func values() -> [RunEvent] { lock.lock(); defer { lock.unlock() }; return items }
}

struct ExecutionGraphTests {
    private func send(_ value: [String: Any], to parser: CLIStreamParser) throws {
        var data = try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys, .withoutEscapingSlashes])
        data.append(10); parser.push(data)
    }
    private func assistant(_ blocks: [[String: Any]], id: String, parent: String? = nil) -> [String: Any] {
        var value: [String: Any] = ["type": "assistant", "uuid": id, "session_id": parent == nil ? "main-session" : "child-session", "message": ["id": id, "content": blocks]]
        if let parent { value["parent_tool_use_id"] = parent }
        return value
    }
    private func spawn(_ id: String, prompt: String? = nil, background: Bool = false) -> [String: Any] {
        var input: [String: Any] = ["description": "Inspect " + id, "run_in_background": background]
        if let prompt { input["prompt"] = prompt }
        return ["type": "tool_use", "id": id, "name": "Agent", "input": input]
    }
    private func toolResult(_ id: String, text: String, parent: String? = nil) -> [String: Any] {
        var value: [String: Any] = ["type": "user", "message": ["content": [["type": "tool_result", "tool_use_id": id, "content": text]]]]
        if let parent { value["parent_tool_use_id"] = parent }
        return value
    }
    private func latest(_ nodes: [ExecutionGraphNode], _ id: String) throws -> ExecutionGraphNode {
        try #require(nodes.last(where: { $0.id == id }))
    }

    @Test func nestedStdoutRoutesVisibleTextAndToolsWithoutFinishingOrResumingMain() throws {
        var nodes: [ExecutionGraphNode] = []; var logs: [String] = []; var resumes: [String] = []; var activities: [AgentActivity] = []
        var results = 0; var now: TimeInterval = 5
        let parser = CLIStreamParser(provider: "claude", log: { logs.append($1) }, resume: { resumes.append($0) }, activityNamespace: "run-one", activity: { activities.append($0) }, result: { results += 1 }, activityClock: { now }, graph: { nodes.append($0) }, graphInput: "Actual main request")
        try send(assistant([spawn("child", prompt: "Read Sources"), ["type": "text", "text": "Main planning"]], id: "main-1"), to: parser)
        let childMessage = assistant([["type": "text", "text": "## Child response\n\nVisible answer"], ["type": "thinking", "thinking": "not-public"], ["type": "tool_use", "id": "read", "name": "Read", "input": ["file_path": "Sources/main.swift"]], spawn("nested", prompt: "Check API")], id: "child-1", parent: "child")
        try send(childMessage, to: parser); let count = nodes.count; try send(childMessage, to: parser)
        #expect(nodes.count == count)
        now = 5.125
        try send(toolResult("read", text: "file output", parent: "child"), to: parser)
        try send(assistant([["type": "text", "text": "Nested answer"]], id: "nested-1", parent: "nested"), to: parser)
        try send(["type": "result", "parent_tool_use_id": "nested", "session_id": "wrong-resume", "is_error": true, "result": "Nested error"], to: parser)
        #expect(results == 0); #expect(!parser.failed); #expect(resumes == ["main-session"])
        #expect(logs == ["Main planning"])
        #expect(!activities.contains { $0.toolName == "Read" })
        let childID = ExecutionGraphSupport.agentNodeID(runId: "run-one", toolUseId: "child")
        let child = try latest(nodes, childID)
        #expect(child.input == "Read Sources")
        #expect(child.entries.filter { $0.kind == "assistant" }.map(\.text) == ["## Child response\n\nVisible answer"])
        #expect(child.entries.first(where: { $0.activity?.toolName == "Read" })?.activity?.durationMs == 125)
        let nested = try latest(nodes, ExecutionGraphSupport.agentNodeID(runId: "run-one", toolUseId: "nested"))
        #expect(nested.parentId == childID); #expect(nested.input == "Check API"); #expect(nested.state == "error")
        #expect(!String(decoding: try JSONEncoder().encode(nodes), as: UTF8.self).contains("not-public"))
        let mainID = ExecutionGraphSupport.mainNodeID(runId: "run-one")
        #expect(try latest(nodes, mainID).output == nil)
        try send(toolResult("child", text: "Actual child final answer"), to: parser)
        try send(["type": "result", "result": "Actual final answer", "is_error": false], to: parser)
        #expect(results == 1)
        #expect(try latest(nodes, mainID).state == "running")
        parser.finishActivities(stopped: false); parser.finishGraph(state: "completed")
        #expect(nodes.last?.id == mainID); #expect(nodes.last?.state == "completed")
        #expect(nodes.last?.output == "Actual final answer"); #expect(nodes.last?.entries.isEmpty == true)
        #expect(nodes.last?.input == "Actual main request")
        #expect(try latest(nodes, childID).output == "Actual child final answer")
    }

    @Test func modsAliasOutOfOrderCompletionAndNestedParentsUseOneNode() throws {
        var nodes: [ExecutionGraphNode] = []; var activities: [AgentActivity] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "mods-run", activity: { activities.append($0) }, graph: { nodes.append($0) })
        parser.receiveMod(ModMetadata(claudeSessionId: "session", event: "tool.call", tool: "Bash", toolUseId: "cmd", summary: "printf fixture", agentId: "agent-a"))
        parser.receiveMod(ModMetadata(claudeSessionId: "session", event: "tool.complete", tool: "Bash", toolUseId: "cmd", output: "fixture", agentId: "agent-a"))
        parser.receiveMod(ModMetadata(claudeSessionId: "session", event: "agent.complete", graph: ModGraphMetadata(phase: "completed", agentId: "agent-a", output: "Actual agent final")))
        parser.receiveMod(ModMetadata(claudeSessionId: "session", event: "agent.spawn", graph: ModGraphMetadata(phase: "starting", parentAgentId: "agent-a", parentToolUseId: "nested", input: "Nested prompt")))
        let childSpawn = ModMetadata(claudeSessionId: "session", event: "agent.spawn", graph: ModGraphMetadata(phase: "running", agentId: "agent-a", parentToolUseId: "child", name: "Research", input: "Original prompt"))
        parser.receiveMod(childSpawn)
        let count = nodes.count; parser.receiveMod(childSpawn)
        #expect(nodes.count == count); #expect(activities.isEmpty)
        let childID = ExecutionGraphSupport.agentNodeID(runId: "mods-run", toolUseId: "child")
        let child = try latest(nodes, childID)
        #expect(child.title == "Research"); #expect(child.input == "Original prompt")
        #expect(child.output == "Actual agent final"); #expect(child.state == "completed")
        #expect(child.entries.count == 1); #expect(child.entries.first?.activity?.state == "completed")
        #expect(try latest(nodes, ExecutionGraphSupport.agentNodeID(runId: "mods-run", toolUseId: "nested")).parentId == childID)
        try send(assistant([["type": "text", "text": "Child streamed answer"]], id: "stream", parent: "child"), to: parser)
        #expect(Set(nodes.filter { $0.kind == "agent" }.map(\.id)).count == 2)
        parser.finishActivities(stopped: true); parser.finishGraph(state: "stopped")
        #expect(try latest(nodes, childID).state == "completed")
        let afterFinish = nodes.count
        parser.receiveMod(childSpawn); parser.finishGraph(state: "completed")
        #expect(nodes.count == afterFinish)
    }

    @Test func backgroundLaunchAcknowledgementIsNotAnAnswerAndMissingPromptRemainsUnknown() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "background", graph: { nodes.append($0) })
        try send(assistant([spawn("background-call", background: true)], id: "main"), to: parser)
        try send(toolResult("background-call", text: "Agent started; output file elsewhere"), to: parser)
        let id = ExecutionGraphSupport.agentNodeID(runId: "background", toolUseId: "background-call")
        #expect(try latest(nodes, id).state == "running")
        #expect(try latest(nodes, id).input == nil); #expect(try latest(nodes, id).output == nil)
        parser.finishActivities(stopped: false); parser.finishGraph(state: "completed")
        let child = try latest(nodes, id)
        #expect(child.state == "stopped"); #expect(child.output == nil)
        #expect(child.entries.last?.kind == "system")
        #expect(nodes.last?.kind == "main"); #expect(nodes.last?.output == nil)
    }

    @Test func stopSettlesChildToolsAndUnmatchedAgentEventsWithoutGuessingIdentity() throws {
        var nodes: [ExecutionGraphNode] = []; var activities: [AgentActivity] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "stopped", activity: { activities.append($0) }, graph: { nodes.append($0) })
        parser.receiveMod(ModMetadata(claudeSessionId: "session", event: "tool.call", tool: "Read", toolUseId: "orphan-tool", summary: "actual.txt", agentId: "orphan-agent"))
        parser.finishActivities(stopped: true); parser.finishGraph(state: "stopped")
        let child = try #require(nodes.last(where: { $0.kind == "agent" }))
        #expect(child.parentId == nil); #expect(child.input == nil); #expect(child.output == nil)
        #expect(child.state == "stopped")
        #expect(child.entries.first?.activity?.state == "stopped"); #expect(activities.isEmpty)
        #expect(nodes.last?.state == "stopped")
    }

    @Test func nodeIDsAreRunScopedAndOtherProvidersEmitNoClaudeGraph() throws {
        #expect(ExecutionGraphSupport.agentNodeID(runId: "one", toolUseId: "shared") != ExecutionGraphSupport.agentNodeID(runId: "two", toolUseId: "shared"))
        for provider in ["codex", "gemini"] {
            var nodes: [ExecutionGraphNode] = []
            let parser = CLIStreamParser(provider: provider, log: { _, _ in }, resume: { _ in }, graph: { nodes.append($0) })
            try send(["type": "result", "status": "success"], to: parser); parser.finishGraph(state: "completed")
            #expect(nodes.isEmpty)
        }
    }

    @Test func graphWireNormalizesBoundsAndRestoresInterruptedSnapshots() throws {
        var node = ExecutionGraphNode(id: "child", runId: "run", parentId: "main", kind: "agent", state: "running", title: String(repeating: "제목", count: 100), input: String(repeating: "한", count: 10_000), output: String(repeating: "🙂", count: 20_000), entries: (0..<120).map { LogEntry(id: "entry-\($0)", kind: "assistant", text: String(repeating: "text", count: 1024), provider: "claude") })
        let normalized = try #require(ExecutionGraphSupport.normalized(node))
        #expect(normalized.input!.utf8.count <= 16_384); #expect(normalized.output!.utf8.count <= 32_768)
        #expect(normalized.entries.count <= 80)
        let textBytes = normalized.title.utf8.count + normalized.input!.utf8.count + normalized.output!.utf8.count + normalized.entries.reduce(0) { $0 + $1.text.utf8.count + $1.id.utf8.count + $1.timestamp.utf8.count + $1.kind.utf8.count + ($1.provider?.utf8.count ?? 0) }
        #expect(textBytes <= 65_536)
        #expect(ExecutionGraphSupport.normalized(normalized) == normalized)
        #expect(ExecutionGraphSupport.normalized(normalized, restoring: true)?.state == "stopped")
        let event = RunEvent(sessionId: "pane", type: "graph", graph: normalized)
        #expect(try JSONDecoder().decode(RunEvent.self, from: JSONEncoder().encode(event)) == event)
        #expect(RemoteValidation.event(event, sessionId: "pane"))
        node.id = "../invalid"; #expect(ExecutionGraphSupport.normalized(node) == nil)
        node.id = "child"; node.updatedAt = "invalid-date"; #expect(ExecutionGraphSupport.normalized(node) == nil)
    }

    @Test func nodeCapDropsChildOutputInsteadOfLeakingItToMain() throws {
        var nodes: [ExecutionGraphNode] = []; var logs: [String] = []; var activities: [AgentActivity] = []
        let parser = CLIStreamParser(provider: "claude", log: { logs.append($1) }, resume: { _ in }, activityNamespace: "bounded", activity: { activities.append($0) }, graph: { nodes.append($0) })
        for index in 0..<140 {
            try send(assistant([["type": "text", "text": "child-only"], ["type": "tool_use", "id": "tool-\(index)", "name": "Read", "input": ["file_path": "a"]]], id: "message-\(index)", parent: "agent-\(index)"), to: parser)
        }
        #expect(Set(nodes.map(\.id)).count == ExecutionGraphSupport.maximumNodes)
        #expect(logs.isEmpty); #expect(activities.isEmpty)
    }

    @Test func delayedCompletionDoesNotDowngradeFailedOrStoppedAgent() throws {
        for state in ["error", "stopped"] {
            var nodes: [ExecutionGraphNode] = []
            let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "terminal-" + state, graph: { nodes.append($0) })
            parser.receiveMod(ModMetadata(claudeSessionId: "session", event: "agent.complete", graph: ModGraphMetadata(phase: state, agentId: "agent", output: "Actual failure")))
            parser.receiveMod(ModMetadata(claudeSessionId: "session", event: "agent.complete", graph: ModGraphMetadata(phase: "completed", agentId: "agent", output: "Late success")))
            parser.receiveMod(ModMetadata(claudeSessionId: "session", event: "agent.spawn", graph: ModGraphMetadata(phase: "running", agentId: "agent", parentToolUseId: "child")))
            try send(["type": "result", "parent_tool_use_id": "child", "result": "Late result", "is_error": false], to: parser)
            let child = try #require(nodes.last(where: { $0.kind == "agent" }))
            #expect(child.state == state); #expect(child.output == "Actual failure")
        }
    }

    @Test func pendingAgentEntriesShareTheSameBoundBeforeSpawnAliasArrives() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "pending-budget", graph: { nodes.append($0) })
        for index in 0..<100 {
            parser.receiveMod(ModMetadata(claudeSessionId: "session", event: "tool.complete", tool: "Read", toolUseId: "tool-\(index)", summary: "file-\(index)", output: String(repeating: "x", count: 8_192), agentId: "agent"))
        }
        parser.receiveMod(ModMetadata(claudeSessionId: "session", event: "agent.complete", graph: ModGraphMetadata(phase: "completed", agentId: "agent", output: String(repeating: "y", count: 32_768))))
        parser.receiveMod(ModMetadata(claudeSessionId: "session", event: "agent.spawn", graph: ModGraphMetadata(phase: "running", agentId: "agent", parentToolUseId: "child")))
        let child = try #require(nodes.last(where: { $0.kind == "agent" }))
        let bytes = (child.output?.utf8.count ?? 0) + child.entries.reduce(0) { $0 + $1.text.utf8.count + ($1.activity?.output?.utf8.count ?? 0) }
        #expect(bytes <= ExecutionGraphSupport.maximumNodeBytes)
        #expect(child.entries.count < 8)
    }

    @Test func realFixtureRunnerEmitsSettledGraphBeforeTerminalStatus() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-graph-process-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let binary = directory.appendingPathComponent("claude")
        let source = #"""
        #!/bin/sh
        if [ "$1" = "--version" ]; then printf '2.1.273\n'; exit 0; fi
        for argument in "$@"; do
          if [ "$argument" = "--safe-mode" ]; then
            IFS= read -r initialize || exit 21
            request_id=$(printf '%s' "$initialize" | /usr/bin/sed -E 's/.*"request_id":"([^"]+)".*/\1/')
            printf '{"type":"control_response","response":{"subtype":"success","request_id":"%s","response":{"models":[]}}}\n' "$request_id"
            /bin/cat >/dev/null; exit 0
          fi
        done
        /bin/cat >/dev/null
        [ "$MIGHTY_CLAUDE_GRAPH" = "1" ] || exit 22
        /bin/cat <<'GRAPH_FIXTURE'
        {"type":"assistant","uuid":"main","session_id":"main-session","message":{"content":[{"type":"tool_use","id":"child","name":"Agent","input":{"prompt":"Actual fixture prompt","description":"Fixture"}}]}}
        {"type":"assistant","uuid":"child-msg","session_id":"child-session","parent_tool_use_id":"child","message":{"content":[{"type":"text","text":"Child fixture response"}]}}
        {"type":"result","parent_tool_use_id":"child","is_error":true,"result":"Child error"}
        {"type":"result","session_id":"main-session","is_error":false,"result":"Root fixture result"}
        GRAPH_FIXTURE
        """#
        try Data(source.utf8).write(to: binary)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
        let plugin = directory.appendingPathComponent("plugin")
        try FileManager.default.createDirectory(at: plugin.appendingPathComponent(".claude-plugin"), withIntermediateDirectories: true)
        try Data("{}".utf8).write(to: plugin.appendingPathComponent(".claude-plugin/plugin.json"))
        let providers = ProviderService(binaryOverrides: ["claude": binary])
        let events = GraphEventRecorder()
        let runner = ProcessRunner(providerService: providers, pluginDirectory: plugin) { events.append($0) }
        do {
            try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "No model execution"), workspace: Workspace(id: "workspace", name: "Fixture", path: directory.path))
            let deadline = Date().addingTimeInterval(6)
            while !events.values().contains(where: { $0.type == "status" && ["completed", "error", "stopped"].contains($0.status ?? "") }) {
                guard Date() < deadline else { throw MightyError("Graph fixture process timed out") }
                try await Task.sleep(for: .milliseconds(20))
            }
            let values = events.values()
            #expect(values.last?.status == "completed")
            #expect(values.filter { $0.type == "resume" }.allSatisfy { $0.resumeId == "main-session" })
            #expect(!values.contains { $0.entry?.text == "Child fixture response" || $0.entry?.text == "Child error" })
            let finalIndex = try #require(values.lastIndex(where: { $0.graph?.kind == "main" && $0.graph?.state == "completed" }))
            #expect(finalIndex < values.count - 1)
            #expect(values[finalIndex].graph?.output == "Root fixture result")
            #expect(values.last(where: { $0.graph?.kind == "agent" })?.graph?.state == "error")
            await runner.shutdown(); await providers.shutdown()
        } catch { await runner.shutdown(); await providers.shutdown(); throw error }
    }
}
