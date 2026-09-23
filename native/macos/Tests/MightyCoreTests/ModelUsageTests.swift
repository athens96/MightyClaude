import Foundation
import Testing
@testable import MightyCore

/// Tests for per-block model/token attribution and ModelUsageFormat formatting.
/// Each test feeds CLI stream JSON through the real ExecutionGraphTracker (via
/// CLIStreamParser) and asserts on ModelUsageFormat output.
struct ModelUsageTests {
    // MARK: - Helpers

    private func send(_ value: [String: Any], to parser: CLIStreamParser) throws {
        var data = try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys, .withoutEscapingSlashes])
        data.append(10)
        parser.push(data)
    }

    /// Build an assistant message event with optional usage and model fields.
    private func assistantMsg(id: String, model: String? = nil, inputTokens: Int = 0, outputTokens: Int = 0, toolUseIds: [String] = [], parent: String? = nil) -> [String: Any] {
        let content: [[String: Any]] = toolUseIds.map { tid in
            ["type": "tool_use", "id": tid, "name": "Agent", "input": ["description": "sub " + tid]]
        }
        let usage: [String: Any] = ["input_tokens": inputTokens, "output_tokens": outputTokens]
        var message: [String: Any] = ["id": id, "content": content, "usage": usage]
        if let m = model { message["model"] = m }
        var event: [String: Any] = ["type": "assistant", "uuid": id, "session_id": "s1", "message": message]
        if let p = parent { event["parent_tool_use_id"] = p }
        return event
    }

    private func toolResult(_ id: String, parent: String? = nil) -> [String: Any] {
        var event: [String: Any] = ["type": "user", "message": ["content": [["type": "tool_result", "tool_use_id": id, "content": "ok"]]]]
        if let p = parent { event["parent_tool_use_id"] = p }
        return event
    }

    private func latest(_ nodes: [ExecutionGraphNode], _ id: String) -> ExecutionGraphNode? {
        nodes.last(where: { $0.id == id })
    }

    // MARK: - Tests

    @Test func perBlockModelsAndTokensFromStream() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "run-m1", graph: { nodes.append($0) })
        let mainID = ExecutionGraphSupport.mainNodeID(runId: "run-m1")

        // R1: opus, 1000 input + 500 output = 1500 total
        try send(assistantMsg(id: "r1", model: "claude-opus-4-5", inputTokens: 1000, outputTokens: 500), to: parser)
        // R2: sonnet, 800 input + 200 output = 1000 total
        try send(assistantMsg(id: "r2", model: "claude-sonnet-4-5", inputTokens: 800, outputTokens: 200), to: parser)

        let node = try #require(latest(nodes, mainID))
        let records = try #require(node.responseRecords)
        #expect(records.count == 2)
        #expect(records[0].model == "claude-opus-4-5")
        #expect(records[0].usage.total == 1500)
        #expect(records[1].model == "claude-sonnet-4-5")
        #expect(records[1].usage.total == 1000)

        let models = ModelUsageFormat.blockModels(records: records)
        #expect(models.count == 2)
        #expect(models[0].model == "claude-opus-4-5")
        #expect(models[0].usage.total == 1500)
        #expect(models[1].model == "claude-sonnet-4-5")
        #expect(models[1].usage.total == 1000)

        let capsule = ModelUsageFormat.blockCapsule(usage: node.usage, records: records, nodeModelLabel: nil)
        #expect(capsule?.contains("2.5K") == true)
        #expect(capsule?.contains("+1") == true)
    }

    @Test func activityLineShowsCallingResponse() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "run-m2", graph: { nodes.append($0) })
        let mainID = ExecutionGraphSupport.mainNodeID(runId: "run-m2")

        // R1: opus with one tool call T1
        try send(assistantMsg(id: "r1", model: "claude-opus-4-5", inputTokens: 1000, outputTokens: 200, toolUseIds: ["t1"]), to: parser)

        let node = try #require(latest(nodes, mainID))
        let records = try #require(node.responseRecords)
        #expect(records.count == 1)
        #expect(records[0].activityIds == ["t1"])

        let suffix = ModelUsageFormat.activitySuffix(activityId: "t1", records: records, childBlock: nil)
        #expect(suffix != nil)
        // Should show model short name + tokens; model not in catalog so raw id used
        #expect(suffix?.contains("claude-opus-4-5") == true)
        #expect(suffix?.contains("1.2K") == true)
    }

    @Test func sharedResponseShowsNumbersOnFirstActivityOnly() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "run-m3", graph: { nodes.append($0) })
        let mainID = ExecutionGraphSupport.mainNodeID(runId: "run-m3")

        // R1 calls two tools T1 and T2
        try send(assistantMsg(id: "r1", model: "claude-opus-4-5", inputTokens: 1500, outputTokens: 0, toolUseIds: ["t1", "t2"]), to: parser)

        let node = try #require(latest(nodes, mainID))
        let records = try #require(node.responseRecords)
        #expect(records[0].activityIds == ["t1", "t2"])

        let first = ModelUsageFormat.activitySuffix(activityId: "t1", records: records, childBlock: nil)
        let second = ModelUsageFormat.activitySuffix(activityId: "t2", records: records, childBlock: nil)

        // First activity shows model and tokens
        #expect(first?.contains("claude-opus-4-5") == true)
        // Second activity shows "same response" marker (no numbers)
        #expect(second == L("usage.modelUsage.sameResponse"))
        // Second does NOT contain digits (no numbers)
        #expect(second?.contains(where: \.isNumber) == false)
    }

    @Test func activityLinesSumToBlockTotal() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "run-m4", graph: { nodes.append($0) })
        let mainID = ExecutionGraphSupport.mainNodeID(runId: "run-m4")

        // R1 calls [T1, T2] — 1000 tokens; only T1 line carries the number
        try send(assistantMsg(id: "r1", model: "claude-opus-4-5", inputTokens: 800, outputTokens: 200, toolUseIds: ["t1", "t2"]), to: parser)
        // R2 calls [T3] — 500 tokens; T3 line carries the number
        try send(assistantMsg(id: "r2", model: "claude-opus-4-5", inputTokens: 400, outputTokens: 100, toolUseIds: ["t3"]), to: parser)
        // R3 calls no activities — 200 tokens; not shown on any activity line
        try send(assistantMsg(id: "r3", model: "claude-opus-4-5", inputTokens: 150, outputTokens: 50), to: parser)

        let node = try #require(latest(nodes, mainID))
        let records = try #require(node.responseRecords)
        #expect(records.count == 3)

        let blockTotal = node.usage?.total ?? 0
        #expect(blockTotal == 1700)

        // Verify the accounting: first-activity totals + no-activity totals = block total
        let responsesWithActivities = records.filter { !$0.activityIds.isEmpty }
        let responsesWithoutActivities = records.filter { $0.activityIds.isEmpty }
        let shownTotal = responsesWithActivities.reduce(0) { $0 + $1.usage.total }
        let unshownTotal = responsesWithoutActivities.reduce(0) { $0 + $1.usage.total }
        #expect(shownTotal + unshownTotal == blockTotal)
    }

    @Test func subagentActivityShowsChildBlockTotal() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "run-m5", graph: { nodes.append($0) })
        let mainID = ExecutionGraphSupport.mainNodeID(runId: "run-m5")
        let agentID = ExecutionGraphSupport.agentNodeID(runId: "run-m5", toolUseId: "agent-tool")

        // Main response spawns an agent via tool_use "agent-tool"
        try send(assistantMsg(id: "r1", model: "claude-opus-4-5", inputTokens: 100, outputTokens: 50, toolUseIds: ["agent-tool"]), to: parser)
        // Agent block response (child of agent-tool) with 3000 tokens
        try send(assistantMsg(id: "r-child", model: "claude-sonnet-4-5", inputTokens: 2500, outputTokens: 500, parent: "agent-tool"), to: parser)

        let mainNode = try #require(latest(nodes, mainID))
        let agentNode = try #require(latest(nodes, agentID))

        let mainRecords = try #require(mainNode.responseRecords)
        let childBlock = (usage: agentNode.usage, records: agentNode.responseRecords ?? [])

        // The activity suffix for "agent-tool" in the main block should show the child total
        let suffix = ModelUsageFormat.activitySuffix(activityId: "agent-tool", records: mainRecords, childBlock: childBlock)
        #expect(suffix != nil)
        // Child block total is 3000 → "3.0K" (compact uses %.1fK for < 10K)
        #expect(agentNode.usage?.total == 3000)
        #expect(suffix?.contains("3.0K") == true)
        // The calling response's own 150 tokens are not what the Task line shows.
        #expect(mainNode.usage?.total == 150)
        #expect(suffix?.contains("150") == false)

        // The same records reach the view through the MightyGraph projection.
        var session = RunSession(workspaceId: "workspace", title: "Claude", provider: "claude")
        session.beginGraphRun(input: "spawn an agent", id: "request", configuredModel: "default")
        for node in nodes { session.recordGraph(RunEvent(sessionId: session.id, type: "graph", graph: node)) }
        let run = try #require(session.mightyGraphRuns.first)
        #expect(run.responseRecords == mainRecords)
        let projectedAgent = try #require(run.agents.first { $0.id == agentID })
        #expect(projectedAgent.responseRecords == agentNode.responseRecords)
        let projectedSuffix = ModelUsageFormat.activitySuffix(
            activityId: "agent-tool",
            records: run.responseRecords ?? [],
            childBlock: (usage: projectedAgent.usage, records: projectedAgent.responseRecords ?? []))
        #expect(projectedSuffix == suffix)
    }

    /// The per-model totals partition the block: their sum is the block total even
    /// when one response carried no model field at all.
    @Test func blockModelTotalsSumToBlockTotal() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "run-m6", graph: { nodes.append($0) })
        let mainID = ExecutionGraphSupport.mainNodeID(runId: "run-m6")

        try send(assistantMsg(id: "r1", model: "claude-opus-4-5", inputTokens: 600, outputTokens: 400), to: parser)
        try send(assistantMsg(id: "r2", model: "claude-sonnet-4-5", inputTokens: 200, outputTokens: 100), to: parser)
        // A response the CLI reported without a "model" field: its tokens still count.
        try send(assistantMsg(id: "r3", inputTokens: 50, outputTokens: 50), to: parser)

        let node = try #require(latest(nodes, mainID))
        let records = try #require(node.responseRecords)
        #expect(records.count == 3)
        #expect(records[2].model == nil)

        let blockTotal = try #require(node.usage?.total)
        #expect(blockTotal == 1400)
        let models = ModelUsageFormat.blockModels(records: records)
        #expect(models.map(\.model) == ["claude-opus-4-5", "claude-sonnet-4-5", ""])
        #expect(models.reduce(0) { $0 + $1.usage.total } == blockTotal)

        // Nothing is invented for the unnamed model: the capsule names the two it knows.
        let capsule = try #require(ModelUsageFormat.blockCapsule(usage: node.usage, records: records, nodeModelLabel: nil))
        #expect(capsule.contains("1.4K"))
        #expect(capsule.contains("claude-opus-4-5 +1"))
        let help = ModelUsageFormat.blockCapsuleHelp(records: records)
        #expect(help.split(separator: "\n").count == 2)
    }

    @Test func capsuleShowsConfiguredModelBeforeFirstResponse() throws {
        let nodeModelLabel = "claude-opus-4-5 · " + L("graph.nodeModel.configuredSuffix")
        let capsule = ModelUsageFormat.blockCapsule(usage: nil, records: [], nodeModelLabel: nodeModelLabel)
        #expect(capsule == nodeModelLabel)

        // After a response arrives, it switches to token-based display
        let record = GraphResponseRecord(responseId: "r1", model: "claude-opus-4-5", usage: GraphTokenUsage(inputTokens: 1000, outputTokens: 200))
        let usage = GraphTokenUsage(inputTokens: 1000, outputTokens: 200)
        let capsuleAfter = ModelUsageFormat.blockCapsule(usage: usage, records: [record], nodeModelLabel: nodeModelLabel)
        #expect(capsuleAfter?.contains("1.2K") == true)
        #expect(capsuleAfter?.contains("claude-opus-4-5") == true)
        #expect(capsuleAfter?.contains(L("graph.nodeModel.configuredSuffix")) == false)
    }

    @Test func codexResponsesUseRunModel() throws {
        var nodes: [ExecutionGraphNode] = []
        let runModel = "gpt-5.6-sol"
        let parser = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in }, activityNamespace: "run-codex", graph: { nodes.append($0) }, configuredModel: runModel)
        let mainID = ExecutionGraphSupport.mainNodeID(runId: "run-codex")

        // Codex turn.completed event
        let turnEvent: [String: Any] = ["type": "turn.completed", "usage": ["input_tokens": 800, "output_tokens": 300]]
        try send(turnEvent, to: parser)

        let node = try #require(latest(nodes, mainID))
        let records = try #require(node.responseRecords)
        #expect(records.count == 1)
        #expect(records[0].model == runModel)
        #expect(records[0].markedAsConfigured == true)
        #expect(records[0].usage.total == 1100)

        let capsule = ModelUsageFormat.blockCapsule(usage: node.usage, records: records, nodeModelLabel: nil)
        #expect(capsule?.contains("1.1K") == true)
        #expect(capsule?.contains(runModel) == true)
    }

    @Test func resentResponseReplacesItsRecord() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "run-resent", graph: { nodes.append($0) })
        let mainID = ExecutionGraphSupport.mainNodeID(runId: "run-resent")

        // Same response ID sent twice with different usage (streaming update)
        try send(assistantMsg(id: "r1", model: "claude-opus-4-5", inputTokens: 100, outputTokens: 50), to: parser)
        // Resent with updated (larger) usage — should replace, not add
        try send(assistantMsg(id: "r1", model: "claude-opus-4-5", inputTokens: 200, outputTokens: 100), to: parser)

        let node = try #require(latest(nodes, mainID))
        let records = try #require(node.responseRecords)
        // Only one record (de-duplicated by response id)
        #expect(records.count == 1)
        #expect(records[0].usage.total == 300)  // 200+100, not 450 (100+50 + 200+100)
        #expect(node.usage?.total == 300)
    }
}
