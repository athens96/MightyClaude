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

    private func command(_ id: String, command: String, description: String? = nil, background: Bool = true) -> [String: Any] {
        var input: [String: Any] = ["command": command, "run_in_background": background]
        if let description { input["description"] = description }
        return ["type": "tool_use", "id": id, "name": "Bash", "input": input]
    }
    private func notification(task: String, tool: String?, status: String, summary: String) -> [String: Any] {
        var body = "[SYSTEM NOTIFICATION]\n<task-notification>\n<task-id>\(task)</task-id>\n"
        if let tool { body += "<tool-use-id>\(tool)</tool-use-id>\n" }
        body += "<output-file>/tmp/\(task).output</output-file>\n<status>\(status)</status>\n<summary>\(summary)</summary>\n</task-notification>"
        return ["type": "user", "message": ["content": [["type": "text", "text": body]]]]
    }

    @Test func backgroundCommandBecomesTaskBlockSettledByNotification() throws {
        var nodes: [ExecutionGraphNode] = []; var activities: [AgentActivity] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "task-run", activity: { activities.append($0) }, graph: { nodes.append($0) })
        try send(assistant([command("build", command: "npm run build", description: "Build the app"), command("sync", command: "rsync -a src/ dst/"), command("quick", command: "ls", background: false)], id: "main-1"), to: parser)
        let mainID = ExecutionGraphSupport.mainNodeID(runId: "task-run")
        let buildID = ExecutionGraphSupport.agentNodeID(runId: "task-run", toolUseId: "build")
        let syncID = ExecutionGraphSupport.agentNodeID(runId: "task-run", toolUseId: "sync")
        #expect(!nodes.contains { $0.id == ExecutionGraphSupport.agentNodeID(runId: "task-run", toolUseId: "quick") })
        let build = try latest(nodes, buildID)
        #expect(build.kind == "task"); #expect(build.state == "running"); #expect(build.parentId == mainID)
        #expect(build.title == "Build the app"); #expect(build.input == "npm run build")
        #expect(try latest(nodes, syncID).title == "rsync -a src/ dst/")
        // The launch acknowledgement is not the command's result.
        try send(toolResult("build", text: "Command running in background with ID: task-b1. Output is being written to: /tmp/task-b1.output."), to: parser)
        try send(toolResult("sync", text: "Command running in background with ID: task-s1. Output is being written to: /tmp/task-s1.output."), to: parser)
        #expect(try latest(nodes, buildID).state == "running"); #expect(try latest(nodes, buildID).output == nil)
        #expect(try latest(nodes, buildID).entries.last?.text.hasPrefix("Command running in background") == true)
        // Main transcript rows still complete on the acknowledgement, as before.
        #expect(activities.contains { $0.toolName == "Bash" && $0.state == "completed" })
        try send(notification(task: "task-b1", tool: "build", status: "completed", summary: "Background command \"Build the app\" completed (exit code 0)"), to: parser)
        let done = try latest(nodes, buildID)
        #expect(done.state == "completed"); #expect(done.output == "Background command \"Build the app\" completed (exit code 0)")
        // A notification that only names the task ID uses the acknowledged alias.
        try send(notification(task: "task-s1", tool: nil, status: "failed", summary: "rsync exited with code 23"), to: parser)
        #expect(try latest(nodes, syncID).state == "error"); #expect(try latest(nodes, syncID).output == "rsync exited with code 23")
        try send(notification(task: "task-b1", tool: "build", status: "failed", summary: "late duplicate"), to: parser)
        #expect(try latest(nodes, buildID).state == "completed")
        #expect(try latest(nodes, mainID).state == "running"); #expect(try latest(nodes, mainID).entries.isEmpty)
    }

    @Test func unfinishedBackgroundCommandStopsWithRunAndLaunchFailureIsAnError() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "task-stop", graph: { nodes.append($0) })
        try send(assistant([command("long", command: "sleep 600", description: "Long job"), command("broken", command: "nope", description: "Broken launch")], id: "main"), to: parser)
        try send(toolResult("long", text: "Command running in background with ID: t-long. Output is being written to: /tmp/t-long.output."), to: parser)
        try send(["type": "user", "message": ["content": [["type": "tool_result", "tool_use_id": "broken", "is_error": true, "content": "command not found: nope"]]]], to: parser)
        let brokenID = ExecutionGraphSupport.agentNodeID(runId: "task-stop", toolUseId: "broken")
        #expect(try latest(nodes, brokenID).state == "error"); #expect(try latest(nodes, brokenID).output == "command not found: nope")
        try send(["type": "result", "result": "Done for now", "is_error": false], to: parser)
        parser.finishActivities(stopped: false); parser.finishGraph(state: "completed")
        let long = try latest(nodes, ExecutionGraphSupport.agentNodeID(runId: "task-stop", toolUseId: "long"))
        #expect(long.kind == "task"); #expect(long.state == "stopped"); #expect(long.output == nil)
        #expect(long.entries.last?.text == "백그라운드 작업의 완료 알림을 받기 전에 실행이 종료되었습니다.")
        #expect(ExecutionGraphSupport.normalized(long, restoring: true)?.kind == "task")
        var unknown = long; unknown.kind = "job"
        #expect(ExecutionGraphSupport.normalized(unknown) == nil)
    }

    @Test func tokenUsageIsSummedPerBlockOncePerMessage() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "tokens", graph: { nodes.append($0) })
        func message(_ id: String, usage: [String: Any], parent: String? = nil, blocks: [[String: Any]] = [["type": "text", "text": "hi"]]) -> [String: Any] {
            var value = assistant(blocks, id: id, parent: parent)
            var inner = value["message"] as! [String: Any]; inner["usage"] = usage; value["message"] = inner
            return value
        }
        // One message streams as several events; the last figure wins, once.
        try send(message("m1", usage: ["input_tokens": 1_000, "output_tokens": 10, "cache_read_input_tokens": 500]), to: parser)
        try send(message("m1", usage: ["input_tokens": 1_000, "output_tokens": 40, "cache_read_input_tokens": 500], blocks: [spawn("child", prompt: "Look")]), to: parser)
        try send(message("m2", usage: ["input_tokens": 1_200, "output_tokens": 25, "cache_creation_input_tokens": 300]), to: parser)
        try send(message("c1", usage: ["input_tokens": 400, "output_tokens": 60], parent: "child"), to: parser)
        try send(message("c1", usage: ["input_tokens": 400, "output_tokens": 60], parent: "child"), to: parser)
        try send(message("bad", usage: ["input_tokens": -5, "output_tokens": "x"]), to: parser)
        let mainID = ExecutionGraphSupport.mainNodeID(runId: "tokens")
        let main = try latest(nodes, mainID)
        #expect(main.usage == GraphTokenUsage(inputTokens: 2_200, outputTokens: 65, cacheReadTokens: 500, cacheCreationTokens: 300))
        let child = try latest(nodes, ExecutionGraphSupport.agentNodeID(runId: "tokens", toolUseId: "child"))
        #expect(child.usage == GraphTokenUsage(inputTokens: 400, outputTokens: 60))
        #expect(GraphTokenUsage.parse(["input_tokens": 0, "output_tokens": 0]) == nil)
        #expect(GraphTokenUsage.compact(999) == "999"); #expect(GraphTokenUsage.compact(1_234) == "1.2K")
        #expect(GraphTokenUsage.compact(12_345) == "12K"); #expect(GraphTokenUsage.compact(1_234_567) == "1.23M")
        var invalid = main; invalid.usage = GraphTokenUsage(inputTokens: -1)
        #expect(ExecutionGraphSupport.normalized(invalid)?.usage == nil)
        parser.finishActivities(stopped: false); parser.finishGraph(state: "completed")
        #expect(try latest(nodes, mainID).usage?.total == 3_065)
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

    private func codexItem(_ event: String, _ item: [String: Any]) -> [String: Any] { ["type": event, "item": item] }
    private func collab(_ id: String, tool: String, sender: String = "root", receivers: [String], prompt: String? = nil, states: [String: [String: Any]] = [:], status: String = "in_progress") -> [String: Any] {
        var item: [String: Any] = ["id": id, "type": "collab_tool_call", "tool": tool, "sender_thread_id": sender, "receiver_thread_ids": receivers, "agents_states": states, "status": status]
        if let prompt { item["prompt"] = prompt }
        return item
    }

    @Test func codexCollabItemsBecomeAgentBlocksAndTurnUsageReachesMain() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in }, activityNamespace: "codex-run", graph: { nodes.append($0) }, graphInput: "Plan the release")
        let mainID = ExecutionGraphSupport.mainNodeID(runId: "codex-run")
        #expect(nodes.first?.title == "Codex CLI" || nodes.first?.title == ProviderOptions.label("codex"))
        try send(["type": "thread.started", "thread_id": "root"], to: parser)
        try send(["type": "turn.started"], to: parser)
        try send(codexItem("item.started", collab("c1", tool: "spawn_agent", receivers: ["t1"], prompt: "Inspect the API", states: ["t1": ["status": "pending_init"]])), to: parser)
        try send(codexItem("item.completed", collab("c1", tool: "spawn_agent", receivers: ["t1"], prompt: "Inspect the API", states: ["t1": ["status": "running"]], status: "completed")), to: parser)
        let t1 = ExecutionGraphSupport.identifier("codex-run", "codex-agent:t1")
        var agent = try latest(nodes, t1)
        #expect(agent.kind == "agent"); #expect(agent.state == "running"); #expect(agent.input == "Inspect the API"); #expect(agent.parentId == mainID)
        try send(codexItem("item.completed", collab("c2", tool: "send_input", receivers: ["t1"], prompt: "Also check auth", states: ["t1": ["status": "running"]], status: "completed")), to: parser)
        #expect(try latest(nodes, t1).entries.map(\.text) == ["Also check auth"])
        // A spawn issued by t1 nests under t1.
        try send(codexItem("item.completed", collab("c3", tool: "spawn_agent", sender: "t1", receivers: ["t2"], prompt: "Nested", states: ["t2": ["status": "running"]], status: "completed")), to: parser)
        let t2 = ExecutionGraphSupport.identifier("codex-run", "codex-agent:t2")
        #expect(try latest(nodes, t2).parentId == t1); #expect(try latest(nodes, t2).input == "Nested")
        try send(codexItem("item.completed", collab("c4", tool: "wait", receivers: ["t1", "t2"], states: ["t1": ["status": "completed", "message": "API inspected"], "t2": ["status": "errored", "message": "boom"]], status: "completed")), to: parser)
        agent = try latest(nodes, t1)
        #expect(agent.state == "completed"); #expect(agent.output == "API inspected")
        #expect(try latest(nodes, t2).state == "error"); #expect(try latest(nodes, t2).output == "boom")
        try send(codexItem("item.completed", collab("c5", tool: "close_agent", receivers: ["t1"], states: [:], status: "completed")), to: parser)
        #expect(try latest(nodes, t1).state == "completed")
        try send(codexItem("item.completed", ["id": "m1", "type": "agent_message", "text": "Release plan ready"]), to: parser)
        try send(["type": "turn.completed", "usage": ["input_tokens": 1_000, "cached_input_tokens": 400, "output_tokens": 50]], to: parser)
        try send(["type": "turn.completed", "usage": ["input_tokens": 200, "output_tokens": 5]], to: parser)
        let main = try latest(nodes, mainID)
        #expect(main.output == "Release plan ready")
        #expect(main.usage == GraphTokenUsage(inputTokens: 1_200, outputTokens: 55, cacheReadTokens: 400))
        parser.finishActivities(stopped: false); parser.finishGraph(state: "completed")
        #expect(nodes.last?.id == mainID); #expect(nodes.last?.state == "completed")
        #expect(Set(nodes.filter { $0.kind == "agent" }.map(\.id)) == [t1, t2])
    }

    @Test func nodeIDsAreRunScopedAndOtherProvidersEmitNoClaudeGraph() throws {
        #expect(ExecutionGraphSupport.agentNodeID(runId: "one", toolUseId: "shared") != ExecutionGraphSupport.agentNodeID(runId: "two", toolUseId: "shared"))
        for provider in ["gemini"] {
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

struct SteeringGraphTests {
    private func send(_ value: [String: Any], to parser: CLIStreamParser) throws {
        var data = try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys, .withoutEscapingSlashes])
        data.append(10); parser.push(data)
    }
    private func assistant(_ blocks: [[String: Any]], id: String) -> [String: Any] {
        ["type": "assistant", "uuid": id, "session_id": "main-session", "message": ["id": id, "content": blocks]]
    }

    @Test func midTurnMessageBecomesABlockUnderMainAndSettlesOnTheNextRootAnswer() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "run-steer", graph: { nodes.append($0) }, graphInput: "Refactor the parser")
        try send(assistant([["type": "tool_use", "id": "tool-1", "name": "Bash", "input": ["command": "sleep 5"]]], id: "main-1"), to: parser)
        parser.steer(id: "first", text: "Also add tests")
        let main = ExecutionGraphSupport.mainNodeID(runId: "run-steer")
        let steer = try #require(nodes.last(where: { $0.kind == "steer" }))
        #expect(steer.parentId == main); #expect(steer.state == "running"); #expect(steer.input == "Also add tests"); #expect(steer.title == "중간 요청")
        // A tool-only assistant message is not the reply.
        try send(assistant([["type": "tool_use", "id": "tool-2", "name": "Read", "input": ["file_path": "/tmp/a"]]], id: "main-2"), to: parser)
        #expect(nodes.last(where: { $0.id == steer.id })?.state == "running")
        // A preamble beside another tool call is not the reply either.
        try send(assistant([["type": "text", "text": "Let me check."], ["type": "tool_use", "id": "tool-3", "name": "Read", "input": ["file_path": "/tmp/b"]]], id: "main-2b"), to: parser)
        #expect(nodes.last(where: { $0.id == steer.id })?.state == "running")
        try send(assistant([["type": "text", "text": "Done, tests added."]], id: "main-3"), to: parser)
        let settled = try #require(nodes.last(where: { $0.id == steer.id }))
        #expect(settled.state == "completed"); #expect(settled.output == "Done, tests added.")
        // Same id twice is one block; the second steer is its own block.
        parser.steer(id: "first", text: "duplicate"); parser.steer(id: "second", text: "One more thing")
        #expect(nodes.filter { $0.kind == "steer" }.map(\.id).reduce(into: Set<String>()) { $0.insert($1) }.count == 2)
        parser.finishGraph(state: "completed")
        let unanswered = try #require(nodes.last(where: { $0.input == "One more thing" }))
        #expect(unanswered.state == "stopped"); #expect(unanswered.entries.last?.text.contains("중간 요청") == true)
    }

    @Test func codexRunsIgnoreSteeringAndSessionsKeepTheSteerKind() throws {
        var nodes: [ExecutionGraphNode] = []
        let codex = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in }, activityNamespace: "run-codex", graph: { nodes.append($0) }, graphInput: "Hi")
        codex.steer(id: "x", text: "ignored")
        #expect(!nodes.contains { $0.kind == "steer" })

        var session = RunSession(workspaceId: "workspace", title: "Claude")
        session.beginGraphRun(input: "Refactor", id: "request-one")
        let main = ExecutionGraphSupport.mainNodeID(runId: "process-one")
        session.recordGraph(RunEvent(sessionId: session.id, type: "graph", graph: ExecutionGraphNode(id: main, runId: "process-one", kind: "main", state: "running", title: "Claude")))
        let steer = ExecutionGraphNode(id: "process-one-steer", runId: "process-one", parentId: main, kind: "steer", state: "completed", title: "중간 요청", input: "Also tests", output: "Sure")
        session.recordGraph(RunEvent(sessionId: session.id, type: "graph", graph: steer))
        let agent = try #require(session.mightyGraphRuns[0].agents.first)
        #expect(agent.isSteer); #expect(agent.parentID == nil); #expect(agent.entries.last?.text == "Sure")
        let restored = try JSONDecoder().decode(RunSession.self, from: try JSONEncoder().encode(session))
        var budget = 1_000_000
        let normalized = MightyGraphSupport.normalized(restored.graphRuns ?? [], restoring: true, budget: &budget)
        #expect(normalized[0].agents[0].isSteer)
    }

    @Test func claudeStdinFramesCarryTheUserMessageShape() throws {
        let data = try ProviderInput.claudeUserMessage("Also add tests")
        let text = String(decoding: data, as: UTF8.self)
        #expect(text.hasSuffix("\n"))
        let object = try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
        #expect(object["type"] as? String == "user")
        #expect((object["message"] as? [String: Any])?["content"] as? String == "Also add tests")
        #expect(object["parent_tool_use_id"] is NSNull)
    }
}
