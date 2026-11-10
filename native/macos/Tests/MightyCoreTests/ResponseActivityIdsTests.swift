import Foundation
import Testing
@testable import MightyCore

/// A response record names the tool calls its message made (`GraphResponseRecord.activityIds`). Claude Code sends
/// a message as several events that share its id, one content block each (its thinking, then each tool call), all
/// with the same usage: the record keeps the union of the tool ids over those events, in the order they came. It
/// used to keep the first event's alone, which for a message that opens with thinking is none, so a tool row lost
/// its model and tokens and an Agent row its child block (`GraphChildBlocks`, `ModelUsageFormat.activitySuffix`).
struct ResponseActivityIdsTests {
    private func send(_ value: [String: Any], to parser: CLIStreamParser) throws {
        var data = try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys, .withoutEscapingSlashes])
        data.append(10); parser.push(data)
    }
    private let usage: [String: Any] = ["input_tokens": 100, "output_tokens": 20, "cache_read_input_tokens": 1_000]
    private func frame(_ message: String, _ blocks: [[String: Any]], uuid: String, usage: [String: Any]? = nil, parent: String? = nil) -> [String: Any] {
        var value: [String: Any] = ["type": "assistant", "uuid": uuid, "session_id": "main",
                                    "message": ["id": message, "model": "claude-haiku-5-5", "content": blocks, "usage": usage ?? self.usage]]
        if let parent { value["parent_tool_use_id"] = parent }
        return value
    }
    private func tool(_ id: String, _ name: String = "Read", input: [String: Any] = ["file_path": "a.txt"]) -> [String: Any] {
        ["type": "tool_use", "id": id, "name": name, "input": input]
    }
    private let thinking: [String: Any] = ["type": "thinking", "thinking": ""]
    private func main(_ nodes: [ExecutionGraphNode]) throws -> ExecutionGraphNode { try #require(nodes.last { $0.kind == "main" }) }

    @Test func aMessageSplitIntoEventsNamesEveryToolCallItMade() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "run", graph: { nodes.append($0) }, graphInput: "request")
        try send(frame("msg-1", [thinking], uuid: "u1"), to: parser)
        #expect(try main(nodes).responseRecords?.map(\.activityIds) == [[]])
        try send(frame("msg-1", [tool("read-a")], uuid: "u2"), to: parser)
        try send(frame("msg-1", [tool("read-b")], uuid: "u3"), to: parser)
        let record = try #require(try main(nodes).responseRecords?.first)
        #expect(try main(nodes).responseRecords?.count == 1)
        #expect(record.activityIds == ["read-a", "read-b"])
        #expect(record.model == "claude-haiku-5-5")
        // The message is still counted once.
        #expect(try main(nodes).usage == GraphTokenUsage(inputTokens: 100, outputTokens: 20, cacheReadTokens: 1_000))
        // The same event again names nothing new and says nothing.
        let emitted = nodes.count
        try send(frame("msg-1", [tool("read-b")], uuid: "u3"), to: parser)
        #expect(nodes.count == emitted)
        #expect(try main(nodes).responseRecords?.first?.activityIds == ["read-a", "read-b"])
    }

    @Test func aMessageWhoseFirstEventNamesItsCallsIsAsItWas() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "run", graph: { nodes.append($0) }, graphInput: "request")
        try send(frame("msg-1", [tool("read-a"), tool("read-b")], uuid: "u1"), to: parser)
        #expect(try main(nodes).responseRecords?.map(\.activityIds) == [["read-a", "read-b"]])
        // Sent again with a larger figure and one more call: counted once at its latest figure, with every call.
        let larger: [String: Any] = ["input_tokens": 150, "output_tokens": 60]
        try send(frame("msg-1", [tool("read-a"), tool("read-b"), tool("read-c")], uuid: "u1", usage: larger), to: parser)
        #expect(try main(nodes).responseRecords?.map(\.activityIds) == [["read-a", "read-b", "read-c"]])
        #expect(try main(nodes).usage == GraphTokenUsage(inputTokens: 150, outputTokens: 60))
        // The next message is its own record.
        try send(frame("msg-2", [thinking], uuid: "u4"), to: parser)
        try send(frame("msg-2", [tool("grep")], uuid: "u5"), to: parser)
        #expect(try main(nodes).responseRecords?.map(\.activityIds) == [["read-a", "read-b", "read-c"], ["grep"]])
        #expect(try main(nodes).usage == GraphTokenUsage(inputTokens: 250, outputTokens: 80, cacheReadTokens: 1_000))
    }

    @Test func anAgentCallAfterItsMessagesThinkingFindsItsChildBlock() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "run", graph: { nodes.append($0) }, graphInput: "request")
        try send(frame("msg-1", [thinking], uuid: "u1"), to: parser)
        try send(frame("msg-1", [tool("agent-a", "Agent", input: ["description": "Count", "prompt": "Go"])], uuid: "u2"), to: parser)
        // The sub-agent's own message, split the same way.
        let childUsage: [String: Any] = ["input_tokens": 7, "output_tokens": 3]
        try send(frame("child-1", [thinking], uuid: "c1", usage: childUsage, parent: "agent-a"), to: parser)
        try send(frame("child-1", [tool("child-read")], uuid: "c2", usage: childUsage, parent: "agent-a"), to: parser)
        let root = try main(nodes)
        let childID = ExecutionGraphSupport.agentNodeID(runId: "run", toolUseId: "agent-a")
        let child = try #require(nodes.last { $0.id == childID })
        #expect(root.responseRecords?.first?.activityIds == ["agent-a"])
        #expect(child.responseRecords?.first?.activityIds == ["child-read"])
        // What reads the ids gets what it expects: the Agent row's child block, and the row's own model and tokens.
        let agent = MightyGraphAgent(id: child.id, title: child.title, status: child.state, usage: child.usage, responseRecords: child.responseRecords)
        let blocks = GraphChildBlocks.map(responseRecords: root.responseRecords, agents: [agent], runId: "run")
        #expect(blocks["agent-a"] == GraphChildBlock(usage: GraphTokenUsage(inputTokens: 7, outputTokens: 3), records: child.responseRecords ?? []))
        LocaleOverride.$language.withValue(.ko) {
            let suffix = ModelUsageFormat.activitySuffix(activityId: "agent-a", records: root.responseRecords ?? [], childBlock: blocks["agent-a"], versioned: true)
            #expect(suffix == "Haiku 5.5 · 1.1K · 하위 10 · Haiku 5.5")
            #expect(ModelUsageFormat.activitySuffix(activityId: "child-read", records: child.responseRecords ?? [], childBlock: nil, versioned: true) == "Haiku 5.5 · 10")
        }
    }

    @Test func aLaterCallOfTheSameMessageReadsAsTheSameResponse() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "run", graph: { nodes.append($0) }, graphInput: "request")
        try send(frame("msg-1", [thinking], uuid: "u1"), to: parser)
        try send(frame("msg-1", [tool("read-a")], uuid: "u2"), to: parser)
        try send(frame("msg-1", [tool("read-b")], uuid: "u3"), to: parser)
        let records = try main(nodes).responseRecords ?? []
        // The first call carries the response's tokens; the second is the same response, counted once.
        #expect(ModelUsageFormat.callerAttribution(activityId: "read-a", records: records) == 1_120)
        #expect(ModelUsageFormat.callerAttribution(activityId: "read-b", records: records) == nil)
        LocaleOverride.$language.withValue(.ko) {
            #expect(ModelUsageFormat.activitySuffix(activityId: "read-b", records: records, childBlock: nil, versioned: true) == L("usage.modelUsage.sameResponse"))
        }
        #expect(ModelUsageFormat.activitySuffix(activityId: "unknown", records: records, childBlock: nil) == nil)
    }

    @Test func theRecordedMessagesNameTheirCalls() throws {
        var session = RunSession(id: "pane", workspaceId: "space", title: "Claude", provider: "claude")
        session.recordGraph(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "request-one", kind: "user", text: "probe", provider: "claude")))
        let box = GraphAgentIdentityTests.Box(session)
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "recorded",
                                     graph: { box.value.recordGraph(RunEvent(sessionId: "pane", type: "graph", graph: $0)) }, graphInput: "probe")
        for line in try GraphAgentIdentityTests.fixture("claude-stream-subagents") { parser.push(line + Data([10])) }
        parser.flush(); parser.finishGraph(state: "completed")
        let run = try #require(box.value.mightyGraphRuns.first)
        // Every recorded message opens with a thinking event; its Agent call comes in the next one.
        let calls = (run.responseRecords ?? []).flatMap(\.activityIds)
        #expect(calls == ["toolu_1", "toolu_3"])
        let blocks = GraphChildBlocks.map(responseRecords: run.responseRecords, agents: run.agents, runId: "recorded")
        #expect(Set(blocks.keys) == ["toolu_1", "toolu_3"])
        #expect(blocks.values.allSatisfy { ($0.usage?.total ?? 0) > 0 })
        // The background agent read two files in one message, after its thinking.
        let background = try #require(run.agents.first { $0.title == "count b and c" })
        #expect((background.responseRecords ?? []).map(\.activityIds).filter { !$0.isEmpty } == [["toolu_4"], ["toolu_5"]])
    }
}
