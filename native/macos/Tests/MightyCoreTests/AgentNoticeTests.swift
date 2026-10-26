import Foundation
import Testing
@testable import MightyCore

/// The file the Windows Core checks read too: a background sub-agent's
/// `<task-notification>` and final report (its hand-back) settle its block the
/// way a background command's notification does, with one 작업 알림 per agent,
/// and the turn a hand-back opens is a notice turn, not the request's. A
/// message the agent sends while still running only joins its block's log.
struct AgentNoticeTests {
    static let url: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/agent-notices.json")
    }()

    static func load() throws -> [String: Any] {
        try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    }

    @Test func handBacksParseAsTheFixtureSays() throws {
        let cases = try #require(try Self.load()["handbacks"] as? [[String: Any]])
        #expect(!cases.isEmpty)
        for item in cases {
            let name = item["name"] as? String ?? ""
            let text = try #require(item["text"] as? String, "\(name)")
            let expected = try #require(item["expect"] as? [[String: Any]], "\(name)")
            let parsed = AgentHandback.parse(text)
            #expect(parsed.map { $0.from } == expected.map { $0["from"] as? String ?? "" }, "\(name)")
            #expect(parsed.map { $0.body } == expected.map { $0["body"] as? String ?? "" }, "\(name)")
            #expect(parsed.map { $0.isHandback } == expected.map { $0["isHandback"] as? Bool ?? false }, "\(name)")
        }
    }

    @Test func noticeResultsAreTheFixtures() throws {
        let cases = try #require(try Self.load()["results"] as? [[String: Any]])
        #expect(!cases.isEmpty)
        for item in cases {
            let name = item["name"] as? String ?? ""
            let event = try #require(item["event"] as? [String: Any], "\(name)")
            #expect(ClaudeStream.isNotificationResult(event) == (item["expect"] as? Bool), "\(name)")
        }
    }

    @Test func streamsSettleTheBlockWithOneNoticePerAgent() throws {
        let cases = try #require(try Self.load()["streams"] as? [[String: Any]])
        #expect(cases.count >= 13)
        for item in cases {
            let name = item["name"] as? String ?? ""
            let runID = "agent-notices"
            var nodes: [ExecutionGraphNode] = []
            let tracker = ExecutionGraphTracker(runID: runID, input: "Do the request", emit: { nodes.append($0) })
            for event in item["events"] as? [[String: Any]] ?? [] { tracker.consume(event) }
            let expect = try #require(item["expect"] as? [String: Any], "\(name)")
            let tool = try #require(item["agentToolUseId"] as? String, "\(name)")
            var latest: [String: ExecutionGraphNode] = [:]
            for node in nodes { latest[node.id] = node }
            let block = try #require(latest[ExecutionGraphSupport.agentNodeID(runId: runID, toolUseId: tool)], "\(name)")
            #expect(block.kind == expect["agentKind"] as? String, "\(name)")
            #expect(block.state == expect["agentState"] as? String, "\(name)")
            #expect(block.output == expect["agentOutput"] as? String, "\(name)")
            // A message the agent sent while running is in its log, not its result.
            for text in expect["agentLog"] as? [String] ?? [] {
                #expect(block.entries.contains { $0.kind == "assistant" && $0.text == text }, "\(name): \(text)")
            }
            // A message that settles the block, or that nobody here sent, is not in its log.
            for text in expect["agentLogLacks"] as? [String] ?? [] {
                #expect(!block.entries.contains { $0.text == text }, "\(name): \(text)")
            }
            let notices = latest.values.filter { $0.kind == "steer" && ($0.input ?? "").hasPrefix(MightyGraphSupport.taskNoticePrefix) }
            #expect(notices.count == expect["noticeBlocks"] as? Int, "\(name)")
            let main = try #require(latest[ExecutionGraphSupport.mainNodeID(runId: runID)], "\(name)")
            for part in expect["mainOutputContains"] as? [String] ?? [] {
                #expect(main.output?.contains(part) == true, "\(name): \(part)")
            }
            // A notice turn's answer follows in the result under the line its notices earned (a locale key, with the titles).
            for section in expect["mainNoticeSections"] as? [[String: Any]] ?? [] {
                let header = L(section["header"] as? String ?? "", (section["title"] as? String).map { ["title": $0] } ?? [:])
                let part = MightyGraphSupport.noticeSectionMark + header + "**\n\n" + (section["answer"] as? String ?? "")
                #expect(main.output?.contains(part) == true, "\(name): \(part)")
            }
        }
    }

    // MARK: History

    private func line(_ object: [String: Any]) throws -> String {
        String(decoding: try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys]), as: UTF8.self)
    }
    private func replay(_ objects: [[String: Any]]) throws -> MightyGraphRun {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("mc-agent-notices-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = folder.appendingPathComponent("record.jsonl")
        try (objects.map { try line($0) }.joined(separator: "\n") + "\n").write(to: url, atomically: true, encoding: .utf8)
        let file = try #require(SessionHistory.identify(url))
        let chunk = try SessionHistory.load(SessionHistoryRequest(provider: "claude", resumeID: "unused", workspacePath: "/unused", file: file, end: nil, anchor: nil, turns: 10))
        return try #require(chunk.runs.first)
    }
    private func stamp(_ seconds: Double) -> String {
        let formatter = ISO8601DateFormatter(); formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: Date(timeIntervalSince1970: 1_788_000_000 + seconds))
    }
    private static let report = "## Docs survey\n\n- help/board.md mentions the board view."
    private static func handback(from sender: String) -> String {
        "<agent-message from=\"\(sender)\">\n[Subagent hand-back] The text below is the final report of a subagent. The report follows:\n"
            + report.split(separator: "\n", omittingEmptySubsequences: false).map { "  " + String($0) }.joined(separator: "\n") + "\n</agent-message>"
    }
    private func agentCall(name: String) -> [String: Any] {
        ["type": "assistant", "uuid": "a1", "timestamp": stamp(1),
         "message": ["id": "m1", "role": "assistant", "content": [["type": "tool_use", "id": "toolu_FAKEagent0001", "name": "Agent",
                                                                   "input": ["description": "Survey the docs", "name": name, "prompt": "Survey the docs.", "run_in_background": true]]]]]
    }
    private func launch(_ agent: String) -> [String: Any] {
        ["type": "user", "uuid": "t1", "timestamp": stamp(2),
         "message": ["role": "user", "content": [["type": "tool_result", "tool_use_id": "toolu_FAKEagent0001",
                                                  "content": [["type": "text", "text": "Async agent launched successfully.\nagentId: \(agent) (internal ID)\nThe agent is working in the background."]]]]]]
    }
    private func answer(_ uuid: String, _ text: String, at seconds: Double) -> [String: Any] {
        ["type": "assistant", "uuid": uuid, "timestamp": stamp(seconds), "message": ["id": "m-" + uuid, "role": "assistant", "content": [["type": "text", "text": text]]]]
    }
    private func check(_ run: MightyGraphRun) throws {
        let agent = try #require(run.agents.first(where: { $0.kind == nil }))
        #expect(agent.status == "completed")
        #expect(agent.entries.last(where: { $0.id.hasSuffix("-answer") })?.text == Self.report)
        #expect(run.agents.filter(\.isTaskNotice).count == 1)
    }

    /// A hand-back recorded as a meta line opens a notice turn, as live: the block takes the report, one 작업 알림, and
    /// that turn's answer follows the request's in the result.
    @Test func aRecordedHandBackTurnSettlesTheAgentAsLive() throws {
        let agent = "a1b2c3d4e5f6a7b8c"
        let notice = "<task-notification>\n<task-id>\(agent)</task-id>\n<tool-use-id>toolu_FAKEagent0001</tool-use-id>\n<status>completed</status>\n<summary>Agent \"Survey the docs\" finished</summary>\n</task-notification>"
        let run = try replay([
            ["type": "user", "uuid": "u1", "timestamp": stamp(0), "message": ["role": "user", "content": "survey the docs"]],
            agentCall(name: "docs-surveyor"), launch(agent),
            answer("a2", "Started the survey.", at: 3),
            ["type": "user", "uuid": "h1", "timestamp": stamp(30), "isMeta": true, "origin": ["kind": "peer", "from": agent, "senderTaskId": agent],
             "message": ["role": "user", "content": "Another Claude session sent a message:\n" + Self.handback(from: agent) + "\n\nThat other Claude session is an agent of this session."]],
            answer("a3", "The survey found one page.", at: 31),
            ["type": "user", "uuid": "n1", "timestamp": stamp(32), "origin": ["kind": "task-notification"], "message": ["role": "user", "content": notice]],
            answer("a4", "Already handled.", at: 33),
        ])
        try check(run)
        #expect(run.finalOutput?.hasPrefix("Started the survey.") == true)
        #expect(run.finalOutput?.contains("The survey found one page.") == true)
    }

    /// A hand-back taken into the running turn is recorded as a queued command; it settles the block by the call's name.
    @Test func aQueuedHandBackSettlesTheAgentByItsName() throws {
        let run = try replay([
            ["type": "user", "uuid": "u1", "timestamp": stamp(0), "message": ["role": "user", "content": "survey the docs"]],
            agentCall(name: "docs-surveyor"), launch("a2b3c4d5e6f7a8b9c"),
            ["type": "attachment", "uuid": "q1", "timestamp": stamp(10), "isMeta": true,
             "attachment": ["type": "queued_command", "commandMode": "prompt", "isMeta": true, "origin": ["kind": "peer", "from": "docs-surveyor"],
                            "prompt": Self.handback(from: "docs-surveyor")]],
            answer("a2", "The survey is done.", at: 11),
        ])
        try check(run)
        #expect(run.finalOutput == "The survey is done.")
    }
}
