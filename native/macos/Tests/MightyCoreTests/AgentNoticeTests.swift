import Foundation
import Testing
@testable import MightyCore

/// The file the Windows Core checks read too: a background sub-agent's
/// `<task-notification>` and final report (its hand-back) settle its block the
/// way a background command's notification does, and the turn either opens is
/// a notice turn with a run of its own (`BackgroundNoticeRuns`), not the
/// request's. A message the agent sends while still running only joins its
/// block's log.
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

    /// The notice runs a stream opened, in the order they were numbered.
    static func noticeRuns(_ latest: [String: ExecutionGraphNode]) -> [(id: String, main: ExecutionGraphNode?, copies: [ExecutionGraphNode])] {
        let byRun = Dictionary(grouping: latest.values, by: \.runId)
        func number(_ id: String) -> Int { Int(id.components(separatedBy: BackgroundNoticeRuns.marker).last ?? "") ?? 0 }
        return byRun.keys.filter { BackgroundNoticeRuns.isNoticeRun($0) }.sorted { number($0) < number($1) }.map { id in
            let nodes = byRun[id] ?? []
            return (id, nodes.first { $0.kind == "main" }, nodes.filter { $0.kind != "main" }.sorted { $0.id < $1.id })
        }
    }

    @Test func streamsSettleTheBlockAndOpenNoticeRuns() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let cases = try #require(try Self.load()["streams"] as? [[String: Any]])
            #expect(cases.count >= 25)
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
                // Started in the background, where its run writes, Claude's answer to its notice: checked when the case names them (null = absent).
                if expect.keys.contains("agentBackground") { #expect(block.background == expect["agentBackground"] as? Bool, "\(name): background") }
                if expect.keys.contains("agentOutputFile") { #expect(block.outputFile == expect["agentOutputFile"] as? String, "\(name): output file") }
                if expect.keys.contains("agentNoticeReply") { #expect(block.noticeReply == expect["agentNoticeReply"] as? String, "\(name): notice reply") }
                if let count = expect["noticeReplyBlocks"] as? Int {
                    #expect(latest.values.filter { $0.noticeReply != nil }.count == count, "\(name): blocks with a notice reply")
                }
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
                // No answer to a notice extends the request's result any more.
                #expect(main.output?.contains(MightyGraphSupport.noticeSectionMark) != true, "\(name): notice section in the request's result")

                // Each notice turn is a run of its own: "<run>:notice:<n>", n from 1.
                let runs = Self.noticeRuns(latest)
                #expect(runs.map(\.id) == runs.indices.map { BackgroundNoticeRuns.runID(original: runID, index: $0 + 1) }, "\(name): run ids")
                if let count = expect["noticeRuns"] as? Int { #expect(runs.count == count, "\(name): notice runs") }
                if let children = expect["noticeRunChildren"] as? [Int] { #expect(runs.map(\.copies.count) == children, "\(name): copies") }
                if expect.keys.contains("noticeRunOutput") { #expect(runs.first?.main?.output == expect["noticeRunOutput"] as? String, "\(name): notice run output") }
                if let input = expect["noticeRunInput"] as? String { #expect(runs.first?.main?.input == input, "\(name): notice run input") }
                if let titles = expect["noticeRunTitles"] as? [String] { #expect(runs.map { $0.main?.title ?? "" } == titles, "\(name): titles") }
                if let outputs = expect["noticeRunOutputs"] as? [Any] {
                    #expect(runs.map { $0.main?.output } == outputs.map { $0 as? String }, "\(name): outputs")
                }
                let kinds = [BackgroundNoticeRuns.Opener.work, .message, .delegation].map(BackgroundNoticeRuns.kind)
                for run in runs {
                    let notice = try #require(run.main, "\(name): \(run.id) main")
                    #expect(kinds.contains { notice.title == $0 || notice.title.hasPrefix($0 + " · ") } && notice.parentId == nil, "\(name): \(run.id) main")
                    // A run with Claude's answer is complete; only a stream that ends inside a turn answering in it again
                    // leaves it running. The request's result never holds that answer.
                    if let output = notice.output {
                        let endsInTurn = (item["events"] as? [[String: Any]])?.last?["type"] as? String != "result"
                        #expect(notice.state == "completed" || (endsInTurn && notice.state == "running"), "\(name): \(run.id) state \(notice.state)")
                        #expect(main.output?.contains(output) != true, "\(name): \(run.id) answer in the request's result")
                    }
                    for copy in run.copies {
                        let source = try #require(copy.sourceBlockId.flatMap { latest[$0] }, "\(name): \(copy.id) source")
                        #expect(copy.id == run.id + ":" + source.id && copy.parentId == notice.id && copy.background == true, "\(name): \(copy.id)")
                    }
                }
                // A block's latest copy is the block as it ended: kind, title, request, state, result and output file.
                for source in Set(runs.flatMap { $0.copies.compactMap(\.sourceBlockId) }) {
                    let copy = try #require(runs.last { $0.copies.contains { $0.sourceBlockId == source } }?.copies.first { $0.sourceBlockId == source })
                    let original = try #require(latest[source])
                    #expect(copy.kind == original.kind && copy.title == original.title && copy.input == original.input, "\(name): \(copy.id) copy")
                    #expect(copy.state == original.state && copy.output == original.output && copy.outputFile == original.outputFile, "\(name): \(copy.id) copy")
                }
            }
        }
    }

    /// The notice run title rule: kind and names in, "<kind> · <name>", "<kind> · <first> 외 <n>개" or the kind alone out.
    @Test func noticeTitlesFollowTheFixture() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let cases = try #require(try Self.load()["noticeTitles"] as? [[String: Any]])
            #expect(cases.count >= 4)
            for item in cases {
                let title = BackgroundNoticeRuns.title(kind: L(item["kind"] as? String ?? ""), names: item["names"] as? [String] ?? [])
                #expect(title == item["expect"] as? String, "\(item["name"] ?? "")")
            }
        }
    }

    /// The result title rule: how the request ended, or 알림 대기 중 while it awaits its background work's notices.
    @Test func resultTitlesFollowTheFixture() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let cases = try #require(try Self.load()["resultTitles"] as? [[String: Any]])
            #expect(cases.count >= 4)
            for item in cases {
                let expect = try #require(item["expect"] as? [String: Any])
                let wanted = L(expect["key"] as? String ?? "")
                #expect(MightyGraphSupport.resultTitle(status: item["status"] as? String ?? "", awaiting: item["awaiting"] as? Bool ?? false) == wanted, "\(item["name"] ?? "")")
            }
        }
    }

    /// The awaiting-notice rule over the shared runs: a request awaits while its background work runs or ended with its
    /// notice turn not answered yet, and not once each was answered, nor when it failed or was stopped
    /// (Windows AgentNoticeVerification.AwaitingNoticeFollowsTheFixture).
    @Test func awaitingNoticeFollowsTheFixture() throws {
        let cases = try #require(try Self.load()["awaitingNotice"] as? [[String: Any]])
        #expect(cases.count >= 8)
        for item in cases {
            let name = item["name"] as? String ?? ""
            let raw = try #require(item["runs"] as? [[String: Any]])
            let runs = try raw.map { try JSONDecoder().decode(MightyGraphRun.self, from: JSONSerialization.data(withJSONObject: $0)) }
            let awaiting = BackgroundNoticeRuns.awaitingNotice(runs[0], in: runs)
            guard let expect = item["expect"] as? [String: Any] else { #expect(awaiting == nil, "awaits nothing: \(name)"); continue }
            let found = try #require(awaiting, "awaits notices: \(name)")
            #expect(found.delivered == expect["delivered"] as? Int, "delivered: \(name)")
            #expect(found.tasks.map(\.status) == expect["statuses"] as? [String], "statuses: \(name)")
            // The shown run says the same, read with the pane's runs.
            #expect(BackgroundNoticeRuns.awaitingNotice(MightyGraphSupport.shownRuns(runs)[0], in: []) == found, "the shown run carries it: \(name)")
        }
    }

    /// A turn that would answer in an earlier notice run but brings new work opens a run for that work; the earlier
    /// run ends as it was before the turn, an error included.
    @Test func aMixedTurnLeavesTheEarlierRunInItsOwnState() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let cases = try #require(try Self.load()["streams"] as? [[String: Any]])
            let mixed = try #require(cases.first { ($0["name"] as? String)?.hasPrefix("notice runs: a turn that also brings a new block") == true })
            var events = try #require(mixed["events"] as? [[String: Any]])
            // The first notice turn fails; stop after the mixed turn's answer.
            let first = try #require(events.firstIndex { $0["type"] as? String == "result" && $0["result"] as? String == "The build passed." })
            events[first]["is_error"] = true
            let mixedEnd = try #require(events.firstIndex { $0["type"] as? String == "result" && $0["result"] as? String == "The checks passed." })
            var nodes: [ExecutionGraphNode] = []
            var afterFirst = false, reopened = false
            let earlier = ExecutionGraphSupport.mainNodeID(runId: "mixed:notice:1")
            let tracker = ExecutionGraphTracker(runID: "mixed", input: "Do the request", emit: { node in
                nodes.append(node)
                if afterFirst, node.id == earlier, node.state == "running" { reopened = true }
            })
            for (index, event) in events.prefix(mixedEnd + 1).enumerated() {
                tracker.consume(event)
                if index == first { afterFirst = true }
            }
            var latest: [String: ExecutionGraphNode] = [:]
            for node in nodes { latest[node.id] = node }
            let runs = Self.noticeRuns(latest)
            #expect(runs.count == 2)
            #expect(reopened, "the turn about the copied build first answers in its run")
            #expect(runs.first?.main?.state == "error" && runs.first?.main?.output == "The build passed.")
            #expect(runs.last?.main?.state == "completed" && runs.last?.main?.output == "The checks passed.")
        }
    }

    // MARK: History

    private func line(_ object: [String: Any]) throws -> String {
        String(decoding: try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys]), as: UTF8.self)
    }
    private func replay(_ objects: [[String: Any]]) throws -> [MightyGraphRun] {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("mc-agent-notices-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = folder.appendingPathComponent("record.jsonl")
        try (objects.map { try line($0) }.joined(separator: "\n") + "\n").write(to: url, atomically: true, encoding: .utf8)
        let file = try #require(SessionHistory.identify(url))
        let chunk = try SessionHistory.load(SessionHistoryRequest(provider: "claude", resumeID: "unused", workspacePath: "/unused", file: file, end: nil, anchor: nil, turns: 10))
        return chunk.runs
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
        #expect(run.agents.filter(\.isTaskNotice).isEmpty)
    }

    /// A hand-back recorded as a meta line opens a notice turn, as live: the block takes the report, and that turn's
    /// answer is the result of a notice run of its own; the notification after it answers in the same run.
    @Test func aRecordedHandBackTurnSettlesTheAgentAsLive() throws {
        let agent = "a1b2c3d4e5f6a7b8c"
        let notice = "<task-notification>\n<task-id>\(agent)</task-id>\n<tool-use-id>toolu_FAKEagent0001</tool-use-id>\n<status>completed</status>\n<summary>Agent \"Survey the docs\" finished</summary>\n</task-notification>"
        let runs = try replay([
            ["type": "user", "uuid": "u1", "timestamp": stamp(0), "message": ["role": "user", "content": "survey the docs"]],
            agentCall(name: "docs-surveyor"), launch(agent),
            answer("a2", "Started the survey.", at: 3),
            ["type": "user", "uuid": "h1", "timestamp": stamp(30), "isMeta": true, "origin": ["kind": "peer", "from": agent, "senderTaskId": agent],
             "message": ["role": "user", "content": "Another Claude session sent a message:\n" + Self.handback(from: agent) + "\n\nThat other Claude session is an agent of this session."]],
            answer("a3", "The survey found one page.", at: 31),
            ["type": "user", "uuid": "n1", "timestamp": stamp(32), "origin": ["kind": "task-notification"], "message": ["role": "user", "content": notice]],
            answer("a4", "Already handled.", at: 33),
        ])
        #expect(runs.count == 2)
        let run = try #require(runs.first)
        try check(run)
        #expect(run.finalOutput == "Started the survey."); #expect(run.noticeRun == nil)
        let notices = Array(runs.dropFirst())
        #expect(notices.map(\.noticeRun) == [true])
        #expect(notices.map(\.finalOutput) == ["The survey found one page.\n\nAlready handled."]); #expect(notices.first?.status == "completed")
        let copy = try #require(notices.first?.agents.first)
        #expect(copy.sourceBlockId == run.agents.first(where: { $0.kind == nil })?.id); #expect(copy.background == true)
        #expect(copy.entries.last(where: { $0.id.hasSuffix("-answer") })?.text == Self.report)
    }

    /// A hand-back taken into the running turn is recorded as a queued command; it settles the block by the call's name,
    /// and its copy goes to a finished notice run without a result.
    @Test func aQueuedHandBackSettlesTheAgentByItsName() throws {
        let runs = try replay([
            ["type": "user", "uuid": "u1", "timestamp": stamp(0), "message": ["role": "user", "content": "survey the docs"]],
            agentCall(name: "docs-surveyor"), launch("a2b3c4d5e6f7a8b9c"),
            ["type": "attachment", "uuid": "q1", "timestamp": stamp(10), "isMeta": true,
             "attachment": ["type": "queued_command", "commandMode": "prompt", "isMeta": true, "origin": ["kind": "peer", "from": "docs-surveyor"],
                            "prompt": Self.handback(from: "docs-surveyor")]],
            answer("a2", "The survey is done.", at: 11),
        ])
        #expect(runs.count == 2)
        let run = try #require(runs.first)
        try check(run)
        #expect(run.finalOutput == "The survey is done.")
        let notice = try #require(runs.last)
        #expect(notice.noticeRun == true); #expect(notice.status == "completed"); #expect(notice.finalOutput == nil); #expect(notice.agents.count == 1)
    }
}
