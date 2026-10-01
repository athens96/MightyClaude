import CoreGraphics
import Darwin
import Foundation
import Testing
@testable import MightyCore

/// Older Mighty requests read back from the CLI's own session record.
struct SessionHistoryTests {
    // MARK: Fixtures

    private func folder() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("mc-history-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }
    private func line(_ object: [String: Any]) -> String {
        String(decoding: try! JSONSerialization.data(withJSONObject: object, options: [.sortedKeys]), as: UTF8.self)
    }
    private static let base = Date(timeIntervalSince1970: 1_788_000_000)
    /// Five minutes apart, so a repeated prompt never falls in a match window.
    private func date(_ turn: Int) -> Date { Self.base.addingTimeInterval(Double(turn) * 300) }
    private func stamp(_ turn: Int, plus seconds: Double = 0) -> String {
        let formatter = ISO8601DateFormatter(); formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: date(turn).addingTimeInterval(seconds))
    }
    private func appStamp(_ date: Date) -> String { ISO8601DateFormatter().string(from: date) }

    /// One Claude request: the prompt, a tool call with its result, an answer.
    private func claudeTurn(_ turn: Int, prompt: String? = nil) -> [String] {
        [
            line(["type": "user", "uuid": "u-\(turn)", "timestamp": stamp(turn), "isSidechain": false,
                  "message": ["role": "user", "content": prompt ?? "요청 \(turn)"]]),
            line(["type": "assistant", "uuid": "a-\(turn)", "timestamp": stamp(turn, plus: 2), "isSidechain": false,
                  "message": ["id": "m-\(turn)", "model": "claude-sonnet-4-5", "role": "assistant",
                              "content": [["type": "tool_use", "id": "tool-\(turn)", "name": "Bash", "input": ["command": "ls \(turn)"]]],
                              "usage": ["input_tokens": 10, "output_tokens": 3]]]),
            line(["type": "user", "uuid": "r-\(turn)", "timestamp": stamp(turn, plus: 3), "toolUseResult": ["stdout": "ok"],
                  "message": ["role": "user", "content": [["type": "tool_result", "tool_use_id": "tool-\(turn)", "content": "ok \(turn)"]]]]),
            line(["type": "assistant", "uuid": "f-\(turn)", "timestamp": stamp(turn, plus: 5), "isSidechain": false,
                  "message": ["id": "n-\(turn)", "model": "claude-sonnet-4-5", "role": "assistant",
                              "content": [["type": "text", "text": "답 \(turn)"]], "usage": ["input_tokens": 12, "output_tokens": 4]]]),
        ]
    }
    /// A record that opens with lines belonging to no request.
    private func claudeRecord(turns: ClosedRange<Int>) -> [String] {
        [line(["type": "queue-operation", "operation": "enqueue", "timestamp": stamp(0)]),
         line(["type": "attachment", "uuid": "x", "timestamp": stamp(0), "attachment": ["type": "hook_success"]])]
            + turns.flatMap { claudeTurn($0) }
    }
    private func write(_ lines: [String], to url: URL, trailing: String = "") throws {
        try (lines.joined(separator: "\n") + "\n" + trailing).write(to: url, atomically: true, encoding: .utf8)
    }
    private func request(_ url: URL, provider: String = "claude", end: Int? = nil, anchor: SessionHistoryAnchor? = nil, turns: Int = 10) throws -> SessionHistoryRequest {
        let file = try #require(SessionHistory.identify(url))
        return SessionHistoryRequest(provider: provider, resumeID: "unused", workspacePath: "/unused", file: file, end: end, anchor: anchor, turns: turns)
    }
    private func inputs(_ chunk: SessionHistoryChunk) -> [String] { chunk.runs.map(\.input) }

    // MARK: Reading backwards

    private func backwards(_ text: String, blockSize: Int, maximumLineBytes: Int = 1_000) throws -> [(Int, String)] {
        let url = try folder().appendingPathComponent("lines.jsonl")
        try text.write(to: url, atomically: true, encoding: .utf8)
        let fd = open(url.path, O_RDONLY)
        defer { close(fd) }
        var reader = JSONLBackwardReader(fd: fd, end: text.utf8.count, blockSize: blockSize, maximumLineBytes: maximumLineBytes)
        var lines: [(Int, String)] = []
        while let (offset, data) = reader.previous() { lines.append((offset, String(decoding: data, as: UTF8.self))) }
        #expect(reader.end == 0)
        return lines
    }

    @Test func linesComeBackNewestFirstWithTheirOffsetsAcrossBlocks() throws {
        for blockSize in [1, 3, 7, 4_096] {
            let lines = try backwards("alpha\nbe\n\ngamma\n", blockSize: blockSize)
            #expect(lines.map(\.1) == ["gamma", "be", "alpha"])
            #expect(lines.map(\.0) == [10, 6, 0])
        }
    }

    @Test func aLineStillBeingWrittenIsNeverReturned() throws {
        for blockSize in [2, 64] {
            #expect(try backwards("one\ntwo\n{\"type\":\"us", blockSize: blockSize).map(\.1) == ["two", "one"])
            // Nothing complete yet at all.
            #expect(try backwards("{\"partial", blockSize: blockSize).isEmpty)
        }
        #expect(try backwards("", blockSize: 8).isEmpty)
    }

    @Test func aLineLongerThanTheLimitIsSkippedWhole() throws {
        let long = String(repeating: "x", count: 40)
        for blockSize in [3, 16, 1_024] {
            #expect(try backwards("first\n\(long)\nlast\n", blockSize: blockSize, maximumLineBytes: 10).map(\.1) == ["last", "first"])
            #expect(try backwards("\(long)\nlast\n", blockSize: blockSize, maximumLineBytes: 10).map(\.1) == ["last"])
        }
    }

    // MARK: Claude records

    @Test func claudeChunksWalkBackTenRequestsAtATimeUntilTheStart() throws {
        let url = try folder().appendingPathComponent("session.jsonl")
        try write(claudeRecord(turns: 1...25), to: url)
        let first = try SessionHistory.load(try request(url))
        #expect(inputs(first) == (16...25).map { "요청 \($0)" })
        #expect(!first.reachedStart)
        let second = try SessionHistory.load(try request(url, end: first.end))
        #expect(inputs(second) == (6...15).map { "요청 \($0)" })
        let third = try SessionHistory.load(try request(url, end: second.end))
        // The lines before the first prompt belong to no request.
        #expect(inputs(third) == (1...5).map { "요청 \($0)" })
        #expect(third.reachedStart)
        #expect(third.end == 0)
        let after = try SessionHistory.load(try request(url, end: third.end))
        #expect(after.runs.isEmpty && after.reachedStart)
    }

    @Test func aReplayedClaudeRequestIsBuiltByTheLiveParserAndTracker() throws {
        let url = try folder().appendingPathComponent("session.jsonl")
        let agentTurn = [
            line(["type": "user", "uuid": "u-1", "timestamp": stamp(1), "message": ["role": "user", "content": "조사해줘"]]),
            line(["type": "assistant", "uuid": "a-1", "timestamp": stamp(1, plus: 1),
                  "message": ["id": "m-1", "model": "claude-sonnet-4-5",
                              "content": [["type": "tool_use", "id": "toolu_agent", "name": "Agent", "input": ["description": "코드 탐색", "prompt": "graph 코드를 찾아"]]],
                              "usage": ["input_tokens": 20, "output_tokens": 5]]]),
            line(["type": "user", "uuid": "r-1", "timestamp": stamp(1, plus: 30), "toolUseResult": ["status": "completed"],
                  "message": ["role": "user", "content": [["type": "tool_result", "tool_use_id": "toolu_agent", "content": "찾았습니다"]]]]),
            line(["type": "system", "subtype": "compact_boundary", "uuid": "c-1", "timestamp": stamp(1, plus: 40),
                  "compactMetadata": ["trigger": "auto", "preTokens": 1_000]]),
            line(["type": "assistant", "uuid": "f-1", "timestamp": stamp(1, plus: 60),
                  "message": ["id": "n-1", "model": "claude-sonnet-4-5", "content": [["type": "text", "text": "정리했습니다"]],
                              "usage": ["input_tokens": 30, "output_tokens": 9]]]),
            // An old-style sub-agent line in the main record is not the request's own.
            line(["type": "assistant", "uuid": "s-1", "isSidechain": true, "timestamp": stamp(1, plus: 61),
                  "message": ["id": "o-1", "content": [["type": "text", "text": "옆길"]]]]),
        ]
        try write(agentTurn, to: url)
        let chunk = try SessionHistory.load(try request(url))
        let run = try #require(chunk.runs.first)
        #expect(run.id == "record-u-1")
        #expect(run.input == "조사해줘")
        #expect(run.status == "completed")
        #expect(run.finalOutput == "정리했습니다")
        #expect(run.resultEntries.first?.text == "정리했습니다")
        let agent = try #require(run.agents.first { $0.kind == nil })
        #expect(agent.title == "코드 탐색")
        #expect(agent.input == "graph 코드를 찾아")
        #expect(agent.status == "completed")
        #expect(run.agents.contains { $0.isCompact })
        #expect(run.usage?.inputTokens == 50)
        #expect(run.nodeModelLabel != nil)
        #expect(!run.rootEntries.contains { $0.text == "옆길" })
        // Entries carry the time the record says, in the app's own format.
        let answer = try #require(run.rootEntries.first { $0.kind == "assistant" && $0.text == "정리했습니다" })
        #expect(answer.timestamp == appStamp(date(1).addingTimeInterval(60)))
        #expect(!run.rootEntries.contains { AgentRunTiming.parseTimestamp($0.timestamp)! > date(1).addingTimeInterval(61) })
    }

    @Test func injectedAndMetaLinesNeverOpenARequest() throws {
        let url = try folder().appendingPathComponent("session.jsonl")
        var lines = claudeTurn(1)
        lines += [
            line(["type": "user", "uuid": "meta", "isMeta": true, "timestamp": stamp(1, plus: 10), "message": ["role": "user", "content": "Continue from where you left off."]]),
            line(["type": "user", "uuid": "note", "origin": ["kind": "task-notification"], "timestamp": stamp(1, plus: 11),
                  "message": ["role": "user", "content": "<task-notification><task-id>t</task-id></task-notification>"]]),
            line(["type": "user", "uuid": "stop", "timestamp": stamp(1, plus: 12),
                  "message": ["role": "user", "content": [["type": "text", "text": "[Request interrupted by user]"]]]]),
            line(["type": "user", "uuid": "cmd", "timestamp": stamp(2),
                  "message": ["role": "user", "content": "<command-message>review</command-message>\n<command-name>/review</command-name>\n<command-args>main</command-args>"]]),
        ]
        try write(lines, to: url)
        let chunk = try SessionHistory.load(try request(url))
        #expect(inputs(chunk) == ["요청 1", "/review main"])
        // The interruption stopped the first request.
        #expect(chunk.runs.first?.status == "stopped")
    }

    // MARK: Overlap with the retained requests

    @Test func theFirstChunkContinuesRightAboveTheOldestRetainedRequest() throws {
        let url = try folder().appendingPathComponent("session.jsonl")
        // The retained requests are 20…25; 22 repeats 20's text.
        let record = claudeRecord(turns: 1...21) + claudeTurn(22, prompt: "요청 20") + (23...25).flatMap { claudeTurn($0) }
        try write(record, to: url)
        let anchor = SessionHistoryAnchor(text: "요청 20\n\n첨부: a.png", date: date(20).addingTimeInterval(-1))
        let chunk = try SessionHistory.load(try request(url, anchor: anchor))
        #expect(inputs(chunk) == (10...19).map { "요청 \($0)" })
        // Text the record wrote differently still lines up by time.
        let unmatched = SessionHistoryAnchor(text: "앱에서 바꾼 요청 문구", date: date(20).addingTimeInterval(1))
        #expect(inputs(try SessionHistory.load(try request(url, anchor: unmatched))) == (10...19).map { "요청 \($0)" })
        // A later chunk ignores the anchor and reads from the cursor.
        let next = try SessionHistory.load(try request(url, end: chunk.end, anchor: anchor))
        #expect(inputs(next) == (1...9).map { "요청 \($0)" })
        #expect(next.reachedStart)
    }

    @Test func anAnchorTheRecordNeverSawLeavesNothingOlder() throws {
        let url = try folder().appendingPathComponent("session.jsonl")
        try write(claudeRecord(turns: 1...3), to: url)
        let chunk = try SessionHistory.load(try request(url, anchor: SessionHistoryAnchor(text: "없는 요청", date: nil)))
        #expect(chunk.runs.isEmpty && chunk.reachedStart)
    }

    @Test func aRecordBeingWrittenIsReadUpToItsLastCompleteLine() throws {
        let url = try folder().appendingPathComponent("session.jsonl")
        try write(claudeRecord(turns: 1...3), to: url, trailing: #"{"type":"user","uuid":"u-9","message":{"role":"user","content":"쓰는 중"#)
        let chunk = try SessionHistory.load(try request(url))
        #expect(inputs(chunk) == ["요청 1", "요청 2", "요청 3"])
    }

    // MARK: Codex rollouts

    private func codexRecord(thread: String) -> [String] {
        func record(_ turn: Int, _ seconds: Double, _ type: String, _ payload: [String: Any]) -> String {
            line(["timestamp": stamp(turn, plus: seconds), "type": type, "payload": payload])
        }
        func codexTurn(_ turn: Int) -> [String] {
            [
                record(turn, 0, "event_msg", ["type": "task_started", "turn_id": "turn-\(turn)"]),
                record(turn, 0, "turn_context", ["turn_id": "turn-\(turn)", "model": "gpt-5-codex"]),
                record(turn, 1, "event_msg", ["type": "item_completed", "item": ["type": "UserMessage", "id": "um-\(turn)", "content": [["type": "text", "text": "코덱스 요청 \(turn)"]]]]),
                record(turn, 2, "event_msg", ["type": "item_completed", "item": ["type": "Reasoning", "id": "rs-\(turn)"]]),
                record(turn, 3, "event_msg", ["type": "item_completed", "item": ["type": "CommandExecution", "id": "exec-\(turn)", "command": ["/bin/zsh", "-lc", "ls -la"], "status": "completed", "aggregated_output": "a\nb", "exit_code": 0]]),
                record(turn, 4, "token_usage_record", ["response_id": "resp-\(turn)-1", "usage": ["input_tokens": 100, "cached_input_tokens": 40, "output_tokens": 7]]),
                record(turn, 5, "token_usage_record", ["response_id": "resp-\(turn)-2", "usage": ["input_tokens": 50, "cached_input_tokens": 0, "output_tokens": 3]]),
                record(turn, 6, "event_msg", ["type": "item_completed", "item": ["type": "AgentMessage", "id": "msg-\(turn)", "phase": "final_answer", "content": [["type": "Text", "text": "끝 \(turn)"]]]]),
                record(turn, 7, "event_msg", ["type": "task_complete", "turn_id": "turn-\(turn)", "last_agent_message": "끝 \(turn)"]),
            ]
        }
        return [line(["timestamp": stamp(0), "type": "session_meta", "payload": ["id": thread]])] + (1...12).flatMap(codexTurn)
    }

    @Test func aCodexRolloutReplaysThroughTheExecParserInChunks() throws {
        let thread = "01a0c1d1-b461-7ad1-8e26-9473d74e2aec"
        let url = try folder().appendingPathComponent("rollout-2026-09-21T11-35-41-\(thread).jsonl")
        try write(codexRecord(thread: thread), to: url)
        let first = try SessionHistory.load(try request(url, provider: "codex"))
        #expect(inputs(first) == (3...12).map { "코덱스 요청 \($0)" })
        let run = try #require(first.runs.last)
        #expect(run.id == "record-turn-12")
        #expect(run.status == "completed")
        #expect(run.finalOutput == "끝 12")
        #expect(run.usage?.inputTokens == 150)
        #expect(run.usage?.outputTokens == 10)
        #expect(run.nodeModelLabel != nil)
        let command = try #require(run.rootEntries.first { $0.activity?.toolName == "command_execution" })
        #expect(command.activity?.state == "completed")
        #expect(command.activity?.summary.contains("ls -la") == true)
        let rest = try SessionHistory.load(try request(url, provider: "codex", end: first.end))
        #expect(inputs(rest) == ["코덱스 요청 1", "코덱스 요청 2"])
        #expect(rest.reachedStart)
    }

    // MARK: Finding the record

    @Test func claudeAndCodexRecordsAreFoundFromThePane() throws {
        let home = try folder()
        let id = "6f1c2a9e-1111-4222-8333-444455556666"
        #expect(SessionHistory.claudeProjectFolder("/Users/me/Work/My_App.v2") == "-Users-me-Work-My-App-v2")
        let direct = home.appendingPathComponent(".claude/projects/-nonexistent-ws-dir", isDirectory: true)
        try FileManager.default.createDirectory(at: direct, withIntermediateDirectories: true)
        try "{}\n".write(to: direct.appendingPathComponent(id + ".jsonl"), atomically: true, encoding: .utf8)
        #expect(SessionHistory.locate(provider: "claude", resumeID: id, workspacePath: "/nonexistent/ws.dir", environment: [:], home: home)?.lastPathComponent == id + ".jsonl")
        // A shortened folder name is still found by the session id alone,
        // under CLAUDE_CONFIG_DIR when it is set.
        let config = home.appendingPathComponent("alt-config", isDirectory: true)
        let other = config.appendingPathComponent("projects/-shortened-12345", isDirectory: true)
        try FileManager.default.createDirectory(at: other, withIntermediateDirectories: true)
        try "{}\n".write(to: other.appendingPathComponent(id + ".jsonl"), atomically: true, encoding: .utf8)
        let found = SessionHistory.locate(provider: "claude", resumeID: id, workspacePath: "/somewhere/else", environment: ["CLAUDE_CONFIG_DIR": config.path], home: home)
        #expect(found?.deletingLastPathComponent().lastPathComponent == "-shortened-12345")
        #expect(SessionHistory.locate(provider: "claude", resumeID: "../escape", workspacePath: "/x", environment: [:], home: home) == nil)

        let thread = "01a0c1d1-b461-7ad1-8e26-9473d74e2aec"
        #expect(SessionHistory.uuidV7Date(thread) != nil)
        let day = home.appendingPathComponent(".codex/sessions/2025/01/02", isDirectory: true)
        try FileManager.default.createDirectory(at: day, withIntermediateDirectories: true)
        try "{}\n".write(to: day.appendingPathComponent("rollout-2025-01-02T03-04-05-\(thread).jsonl"), atomically: true, encoding: .utf8)
        #expect(SessionHistory.locate(provider: "codex", resumeID: thread, workspacePath: "/x", environment: [:], home: home)?.lastPathComponent.hasSuffix(thread + ".jsonl") == true)
        #expect(SessionHistory.locate(provider: "gemini", resumeID: thread, workspacePath: "/x", environment: [:], home: home) == nil)
    }

    @Test func aMissingOrReplacedRecordIsReportedNotRead() throws {
        let home = try folder()
        let missing = SessionHistoryRequest(provider: "claude", resumeID: "6f1c2a9e-1111-4222-8333-444455556666", workspacePath: "/x", home: home)
        #expect(throws: SessionHistoryError.missing) { try SessionHistory.load(missing) }
        let unsupported = SessionHistoryRequest(provider: "gemini", resumeID: "s", workspacePath: "/x", home: home)
        #expect(throws: SessionHistoryError.unsupported) { try SessionHistory.load(unsupported) }

        let url = home.appendingPathComponent("session.jsonl")
        try write(claudeRecord(turns: 1...3), to: url)
        let stale = try request(url, end: 10)
        // Rotated: a new file under the same name.
        try write(claudeRecord(turns: 1...2), to: url)
        #expect(throws: SessionHistoryError.changed) { try SessionHistory.load(stale) }
        // Shortened below the cursor.
        let cursor = try request(url, end: 1_000_000)
        #expect(throws: SessionHistoryError.changed) { try SessionHistory.load(cursor) }
        // Removed after a chunk was read.
        let gone = try request(url, end: 10)
        try FileManager.default.removeItem(at: url)
        #expect(throws: SessionHistoryError.changed) { try SessionHistory.load(gone) }
    }

    // MARK: A pane's loaded history

    private func chunk(_ ids: [String], end: Int, start: Bool) -> SessionHistoryChunk {
        SessionHistoryChunk(runs: ids.map { MightyGraphRun(id: $0, input: $0, status: "completed") },
                            file: SessionHistoryFile(url: URL(fileURLWithPath: "/r.jsonl"), device: 1, inode: 2), end: end, reachedStart: start)
    }

    @Test func chunksArePrependedOldestFirstUntilTheStart() throws {
        var state = SessionHistoryState()
        let base = SessionHistoryRequest(provider: "claude", resumeID: "", workspacePath: "/w")
        let anchor = SessionHistoryAnchor(text: "a", date: nil)
        let begun = state.begin(anchorRunID: "a", resumeID: "s", anchor: anchor, base: base)
        let first = try #require(begun)
        #expect(first.anchor == anchor && first.end == nil && first.file == nil)
        #expect(state.phase == .loading)
        // One load at a time.
        let duplicate = state.begin(anchorRunID: "a", resumeID: "s", anchor: anchor, base: base)
        #expect(duplicate == nil)
        state.finish(.success(chunk(["x", "y"], end: 500, start: false)), generation: state.generation)
        #expect(state.runs.map(\.id) == ["x", "y"])
        #expect(state.phase == .idle)
        let next = state.begin(anchorRunID: "a", resumeID: "s", anchor: anchor, base: base)
        let second = try #require(next)
        #expect(second.anchor == nil && second.end == 500 && second.file != nil)
        state.finish(.success(chunk(["v", "w", "x"], end: 0, start: true)), generation: state.generation)
        #expect(state.runs.map(\.id) == ["v", "w", "x", "y"])
        #expect(state.phase == .start)
        #expect(!state.canLoad)
        #expect(state.connects(anchorRunID: "a", resumeID: "s"))
        #expect(!state.connects(anchorRunID: "b", resumeID: "s"))
    }

    @Test func aTrimmedAnchorAnotherSessionOrAReplacedRecordStartsOver() throws {
        var state = SessionHistoryState()
        let base = SessionHistoryRequest(provider: "claude", resumeID: "", workspacePath: "/w")
        _ = state.begin(anchorRunID: "a", resumeID: "s", anchor: nil, base: base)
        let stale = state.generation
        state.finish(.success(chunk(["x"], end: 9, start: false)), generation: stale)
        // The retained list now starts at another request.
        let restarted = state.begin(anchorRunID: "b", resumeID: "s", anchor: nil, base: base)
        let again = try #require(restarted)
        #expect(again.end == nil && state.runs.isEmpty)
        // A result from before the reset is dropped.
        let late = state.finish(.success(chunk(["late"], end: 1, start: false)), generation: stale)
        #expect(!late)
        #expect(state.phase == .loading)
        let restart = state.finish(.failure(SessionHistoryError.changed), generation: state.generation)
        #expect(restart)
        #expect(state.phase == .idle && state.runs.isEmpty)
        _ = state.begin(anchorRunID: "b", resumeID: "s", anchor: nil, base: base)
        state.finish(.failure(SessionHistoryError.missing), generation: state.generation)
        #expect(state.phase == .unavailable)
        // No session record id yet: nothing to read.
        var fresh = SessionHistoryState()
        let none = fresh.begin(anchorRunID: "a", resumeID: nil, anchor: nil, base: base)
        #expect(none == nil)
        #expect(fresh.phase == .unavailable)
        _ = fresh.begin(anchorRunID: "a", resumeID: "s2", anchor: nil, base: base)
        #expect(fresh.phase == .loading)
        fresh.finish(.failure(CocoaError(.fileReadUnknown)), generation: fresh.generation)
        #expect(fresh.phase == .failed && fresh.canLoad)
    }

    // MARK: Drawing them above

    private func run(_ id: String) -> MightyGraphRun {
        MightyGraphRun(id: id, input: "요청 " + id, status: "completed", agents: [MightyGraphAgent(id: id + "-agent", status: "completed")], finalOutput: "결과 " + id)
    }

    @Test func olderRequestsGoAboveWithoutMovingAnythingOnScreen() throws {
        let retained = [run("a"), run("b")]
        let older = [run("record-1"), run("record-2")]
        let plain = MightyGraphLayout.make(runs: retained, draft: "", running: false, expanded: [])
        let joined = MightyGraphLayout.make(runs: older + retained, draft: "", running: false, expanded: [], retainedStart: 2, history: true)
        for node in plain.nodes {
            #expect(joined.nodes.first { $0.id == node.id }?.frame == node.frame)
        }
        let olderFrames = joined.nodes.filter { $0.id.contains(":record-") }.map(\.frame)
        #expect(!olderFrames.isEmpty && olderFrames.allSatisfy { $0.maxY < 24 })
        // One continuous diagram: the last older result joins the first retained request.
        let olderResult = MightyGraphBlockSize.nodeID(runID: "record-2", suffix: "result")
        let firstRequest = MightyGraphBlockSize.nodeID(runID: "a", suffix: "request")
        #expect(joined.edges.contains { $0.source == olderResult && $0.target == firstRequest && $0.joins })
        let history = try #require(joined.nodes.first { $0.content == .history })
        #expect(history.id == MightyGraphLayout.historyNodeID)
        #expect(joined.nodes.allSatisfy { $0.id == history.id || $0.frame.minY > history.frame.maxY })
        #expect(joined.originY == history.frame.minY - 24)
        #expect(joined.size.height == plain.size.height - joined.originY)
        // Without older requests the history block alone sits above the first card.
        let alone = MightyGraphLayout.make(runs: retained, draft: "", running: false, expanded: [], history: true)
        #expect(alone.nodes.first { $0.content == .history }.map { $0.frame.maxY < 24 } == true)
        #expect(MightyGraphCamera.isAuxiliary(nodeID: MightyGraphLayout.historyNodeID))
    }

    @Test func loadingOlderRequestsHoldsTheCameraAndTheTopAsksForMore() {
        let previous = ["a", "b"]
        #expect(MightyGraphCamera.trimAnchor(previousRunIDs: previous, runIDs: ["o1", "o2", "a", "b"], selectedNodeID: nil,
                                             layoutNodeIDs: []) == .hold)
        // A real trim still re-aims.
        let newest = MightyGraphBlockSize.nodeID(runID: "b", suffix: "request")
        #expect(MightyGraphCamera.trimAnchor(previousRunIDs: ["o1", "a", "b"], runIDs: ["a", "b"], selectedNodeID: nil,
                                             layoutNodeIDs: [newest]) == .reaim(nodeID: newest, alignTop: true))
        // Camera y is the screen position of node y = 0: the top shows once
        // the viewport starts at or above it.
        #expect(MightyGraphCamera.showsTop(camera: CGPoint(x: 0, y: 500), zoom: 1, top: -500))
        #expect(!MightyGraphCamera.showsTop(camera: CGPoint(x: 0, y: 400), zoom: 1, top: -500))
        #expect(MightyGraphCamera.showsTop(camera: CGPoint(x: 0, y: 250), zoom: 0.5, top: -500))
        #expect(!MightyGraphCamera.showsTop(camera: CGPoint(x: 0, y: 0), zoom: 0, top: 0))
    }

    // MARK: Review fixes

    private func claudeAt(_ uuid: String, _ when: Date, _ prompt: String, extra: [String: Any] = [:]) -> [String] {
        var opening: [String: Any] = ["type": "user", "uuid": uuid, "timestamp": appStamp(when), "isSidechain": false,
                                      "message": ["role": "user", "content": prompt]]
        opening.merge(extra) { _, new in new }
        return [line(opening),
                line(["type": "assistant", "uuid": uuid + "-a", "timestamp": appStamp(when.addingTimeInterval(2)), "isSidechain": false,
                      "message": ["id": uuid + "-m", "model": "claude-sonnet-4-5", "role": "assistant",
                                  "content": [["type": "text", "text": "답 " + uuid]], "usage": ["input_tokens": 1, "output_tokens": 1]]])]
    }

    @Test func interactiveClaudeTypedPromptsLocalCommandsAndInjectedLines() throws {
        let url = try folder().appendingPathComponent("session.jsonl")
        let human: [String: Any] = ["origin": ["kind": "human"], "promptSource": "typed", "entrypoint": "cli"]
        var lines = claudeAt("u1", date(1), "첫 요청", extra: human)
        lines += [
            line(["type": "user", "uuid": "cmd", "timestamp": stamp(2), "entrypoint": "cli",
                  "message": ["role": "user", "content": "<command-name>/model</command-name>\n<command-message>model</command-message>\n<command-args></command-args>"]]),
            line(["type": "user", "uuid": "out", "timestamp": stamp(2, plus: 1), "entrypoint": "cli",
                  "message": ["role": "user", "content": "<local-command-stdout>Set model to \u{1B}[1mopus\u{1B}[22m</local-command-stdout>"]]),
            line(["type": "user", "uuid": "err", "timestamp": stamp(2, plus: 2), "entrypoint": "cli",
                  "message": ["role": "user", "content": "<local-command-stderr>oops</local-command-stderr>"]]),
            line(["type": "user", "uuid": "note", "timestamp": stamp(2, plus: 3), "origin": ["kind": "task-notification"],
                  "message": ["role": "user", "content": "<task-notification>done</task-notification>"]]),
            line(["type": "user", "uuid": "peer", "timestamp": stamp(2, plus: 4), "origin": ["kind": "peer", "from": "x"], "turnOrigin": "peer",
                  "message": ["role": "user", "content": "다른 에이전트의 메시지"]]),
        ]
        lines += claudeAt("u3", date(3), "둘째 요청", extra: ["origin": ["kind": "human"], "promptSource": "queued", "entrypoint": "cli"])
        lines += claudeAt("u4", date(4), "please explain <command-name>x</command-name> here")
        try write(lines, to: url)
        let chunk = try SessionHistory.load(try request(url))
        #expect(inputs(chunk) == ["첫 요청", "/model", "둘째 요청", "please explain <command-name>x</command-name> here"])
        // The local command's printed output is its result, not a request.
        let model = try #require(chunk.runs.first { $0.input == "/model" })
        #expect(model.finalOutput == "oops")
        #expect(chunk.runs.allSatisfy { !$0.input.hasPrefix("<local-command") })
        #expect(HistoryScan.localCommandOutput("<local-command-stdout>Set model to \u{1B}[1mopus\u{1B}[22m</local-command-stdout>") == "Set model to opus")
        #expect(HistoryScan.localCommandOutput("평범한 요청") == nil)
    }

    @Test func queuedSteersReplayAsSteerBlocks() throws {
        let url = try folder().appendingPathComponent("session.jsonl")
        var lines = claudeTurn(1)
        let answer = lines.removeLast()
        lines += [
            line(["type": "attachment", "uuid": "q1", "timestamp": stamp(1, plus: 4), "isSidechain": false,
                  "attachment": ["type": "queued_command", "commandMode": "prompt", "prompt": "이것도 봐줘", "source_uuid": "s"]]),
            line(["type": "attachment", "uuid": "q2", "timestamp": stamp(1, plus: 4), "isSidechain": false,
                  "attachment": ["type": "queued_command", "commandMode": "prompt", "isMeta": true, "origin": ["kind": "peer"], "prompt": "<peer-message>x</peer-message>"]]),
            line(["type": "attachment", "uuid": "q3", "timestamp": stamp(1, plus: 4), "isSidechain": false,
                  "attachment": ["type": "queued_command", "commandMode": "task-notification", "prompt": "<task-notification/>"]]),
            answer,
        ]
        try write(lines, to: url)
        let chunk = try SessionHistory.load(try request(url))
        #expect(inputs(chunk) == ["요청 1"])
        let steers = try #require(chunk.runs.first).agents.filter(\.isSteer)
        #expect(steers.map(\.input) == ["이것도 봐줘"])
    }

    private func codexLine(_ type: String, _ payload: [String: Any]) -> Data {
        Data(line(["timestamp": stamp(1), "type": type, "payload": payload]).utf8)
    }
    private func codexResponse(_ text: String) -> Data {
        codexLine("response_item", ["type": "message", "role": "user", "content": [["type": "input_text", "text": text]]])
    }
    private func codexItem(_ text: String) -> Data {
        codexLine("event_msg", ["type": "item_completed", "item": ["type": "UserMessage", "id": "m", "content": [["type": "text", "text": text]]]])
    }
    private func codexEvent(_ text: String) -> Data {
        codexLine("event_msg", ["type": "user_message", "message": text, "images": [], "local_images": [], "text_elements": []])
    }

    @Test func codexPromptReadsEveryRolloutShape() throws {
        let context = codexResponse("<environment_context>\n<cwd>/w</cwd>\n</environment_context>")
        // 0.147: response items, then the user_message event.
        #expect(HistoryScan.codexPrompt([context, codexResponse("응답 147"), codexEvent(" 이벤트 147 ")]) == "이벤트 147")
        // 0.153: the UserMessage item wins over what the model saw.
        #expect(HistoryScan.codexPrompt([context, codexResponse("응답 153"), codexItem("아이템 153")]) == "아이템 153")
        // 0.159: AGENTS.md instructions come first as a plain user message.
        let agents = codexResponse("# AGENTS.md instructions for /w\n\n<INSTRUCTIONS>…</INSTRUCTIONS>")
        #expect(HistoryScan.codexPrompt([agents, codexResponse("응답 159"), codexItem("아이템 159")]) == "아이템 159")
        #expect(HistoryScan.codexPrompt([agents, codexResponse("응답 159")]) == "응답 159")
        #expect(HistoryScan.codexPrompt([context]) == nil)
        // A 0.147 rollout replays with its event text as the request.
        let thread = "01a0c1d1-b461-7ad1-8e26-9473d74e2aec"
        let url = try folder().appendingPathComponent("rollout-2026-09-21T11-35-41-\(thread).jsonl")
        let old = [line(["timestamp": stamp(0), "type": "session_meta", "payload": ["id": thread, "cli_version": "0.147.0"]])]
            + [1, 2].flatMap { turn in [
                line(["timestamp": stamp(turn), "type": "event_msg", "payload": ["type": "task_started", "turn_id": "t\(turn)"]]),
                String(decoding: context, as: UTF8.self),
                line(["timestamp": stamp(turn), "type": "response_item", "payload": ["type": "message", "role": "user", "content": [["type": "input_text", "text": "옛 요청 \(turn)"]]]]),
                line(["timestamp": stamp(turn), "type": "event_msg", "payload": ["type": "user_message", "message": "옛 요청 \(turn)"]]),
                line(["timestamp": stamp(turn, plus: 1), "type": "event_msg", "payload": ["type": "task_complete", "turn_id": "t\(turn)"]]),
            ] }
        try write(old, to: url)
        #expect(inputs(try SessionHistory.load(try request(url, provider: "codex"))) == ["옛 요청 1", "옛 요청 2"])
    }

    @Test func theSameTextBeatsAContainingOneAndTheClosestTimeWins() throws {
        let url = try folder().appendingPathComponent("session.jsonl")
        let sent = date(10)
        let record = claudeAt("old", sent.addingTimeInterval(-900), "옛 요청")
            + claudeAt("x", sent.addingTimeInterval(-200), "고쳐줘 그리고 더")
            + claudeAt("z", sent.addingTimeInterval(-100), "고쳐줘")
            + claudeAt("y", sent.addingTimeInterval(5), "고쳐줘")
            + claudeAt("new", sent.addingTimeInterval(400), "다음 요청")
        try write(record, to: url)
        let anchor = SessionHistoryAnchor(text: "고쳐줘", date: sent)
        #expect(inputs(try SessionHistory.load(try request(url, anchor: anchor))) == ["옛 요청", "고쳐줘 그리고 더", "고쳐줘"])
        // Without dates the same text is still preferred over a containing one.
        let undated = try folder().appendingPathComponent("undated.jsonl")
        let plain = ["옛 요청", "고쳐줘", "고쳐줘 그리고 더"].enumerated().map { index, text in
            line(["type": "user", "uuid": "p\(index)", "message": ["role": "user", "content": text]])
        }
        try write(plain, to: undated)
        let chunk = try SessionHistory.load(try request(undated, anchor: SessionHistoryAnchor(text: "고쳐줘", date: nil)))
        #expect(inputs(chunk) == ["옛 요청"])
        #expect(chunk.reachedStart)
    }

    @Test func aCancelledLoadStopsBetweenRequests() async throws {
        let url = try folder().appendingPathComponent("session.jsonl")
        try write(claudeRecord(turns: 1...3), to: url)
        let request = try request(url)
        let task = Task.detached { () throws -> SessionHistoryChunk in
            withUnsafeCurrentTask { $0?.cancel() }
            return try SessionHistory.load(request)
        }
        await #expect(throws: CancellationError.self) { try await task.value }
    }

    @Test func aPaneThatLoadedBeforeItsFirstRequestKeepsItsHistory() throws {
        var state = SessionHistoryState()
        let base = SessionHistoryRequest(provider: "claude", resumeID: "", workspacePath: "/w")
        let begun = state.begin(anchorRunID: nil, resumeID: "s", anchor: nil, base: base)
        #expect(begun != nil)
        state.finish(.success(chunk(["x", "y"], end: 9, start: false)), generation: state.generation)
        // The first request the resumed pane sends becomes the anchor.
        #expect(state.connects(anchorRunID: "new", resumeID: "s"))
        #expect(!state.connects(anchorRunID: "new", resumeID: "other"))
        state.follow(previous: [], current: [MightyGraphRun(id: "new", input: "n")], resumeID: "s", provider: "claude")
        #expect(state.anchorRunID == "new" && state.pinnedRunID == "new")
        #expect(state.runs.map(\.id) == ["x", "y"])
        // A later load keeps going from the cursor.
        let next = state.begin(anchorRunID: "new", resumeID: "s", anchor: nil, base: base)
        #expect(next?.end == 9)
        // Without follow, reconcile adopts too.
        var other = SessionHistoryState()
        _ = other.begin(anchorRunID: nil, resumeID: "s", anchor: nil, base: base)
        other.finish(.success(chunk(["x"], end: 3, start: false)), generation: other.generation)
        other.reconcile(anchorRunID: "first", resumeID: "s")
        #expect(other.anchorRunID == "first" && other.runs.map(\.id) == ["x"])
    }

    @Test func aTrimMovesTheDroppedRunsIntoTheHistoryWithoutMovingAnything() throws {
        var state = SessionHistoryState()
        let base = SessionHistoryRequest(provider: "claude", resumeID: "", workspacePath: "/w")
        _ = state.begin(anchorRunID: "a", resumeID: "s", anchor: nil, base: base)
        state.finish(.success(SessionHistoryChunk(runs: [run("record-x"), run("record-y")], file: SessionHistoryFile(url: URL(fileURLWithPath: "/r"), device: 1, inode: 2),
                                                  end: 40, reachedStart: false)), generation: state.generation)
        let before = [run("a"), run("b"), run("c")]
        let after = [run("c"), run("d")]
        state.follow(previous: before, current: after, resumeID: "s", provider: "claude")
        #expect(state.runs.map(\.id) == ["record-x", "record-y", "a", "b"])
        #expect(state.anchorRunID == "c" && state.pinnedRunID == "a" && state.phase == .idle)
        #expect(state.connects(anchorRunID: "c", resumeID: "s"))
        // The run the history first attached above keeps its place, and so
        // does everything above it and the runs that moved in below it (the
        // latest result card is another run's now, so it is left out).
        let old = MightyGraphLayout.make(runs: [run("record-x"), run("record-y")] + before, draft: "", running: false, expanded: [], retainedStart: 2, history: true)
        let new = MightyGraphLayout.make(runs: state.runs + after, draft: "", running: false, expanded: [], retainedStart: 2, history: true)
        let kept = ["record-x", "record-y", "a", "b"].map { MightyGraphBlockSize.nodeID(runID: $0, suffix: "") }
        for node in old.nodes where kept.contains(where: { node.id.hasPrefix($0) }) {
            #expect(new.nodes.first { $0.id == node.id }?.frame == node.frame)
        }
        // A trim of runs the history never attached to starts over.
        state.follow(previous: [run("q"), run("d")], current: [run("d")], resumeID: "s", provider: "claude")
        #expect(state.runs.isEmpty && state.anchorRunID == nil)
    }

    @Test func historyStaysWithinItsCountAndByteLimits() throws {
        let file = SessionHistoryFile(url: URL(fileURLWithPath: "/r"), device: 1, inode: 2)
        let base = SessionHistoryRequest(provider: "claude", resumeID: "", workspacePath: "/w")
        var state = SessionHistoryState()
        _ = state.begin(anchorRunID: "a", resumeID: "s", anchor: nil, base: base)
        let many = (0..<(SessionHistoryState.maximumRuns + 5)).map { MightyGraphRun(id: "r\($0)", input: "r", status: "completed") }
        state.finish(.success(SessionHistoryChunk(runs: many, file: file, end: 10, reachedStart: false)), generation: state.generation)
        #expect(state.runs.count == SessionHistoryState.maximumRuns)
        #expect(state.runs.first?.id == "r5" && state.phase == .limit && !state.canLoad)

        var heavy = SessionHistoryState()
        _ = heavy.begin(anchorRunID: "a", resumeID: "s", anchor: nil, base: base)
        let big = String(repeating: "가", count: 600_000)
        let runs = (0..<3).map { MightyGraphRun(id: "h\($0)", input: "h", status: "completed", finalOutput: big) }
        heavy.finish(.success(SessionHistoryChunk(runs: runs, file: file, end: 10, reachedStart: false)), generation: heavy.generation)
        #expect(heavy.runs.map(\.id) == ["h2"])
        #expect(heavy.phase == .limit)

        var broken = SessionHistoryState()
        _ = broken.begin(anchorRunID: "a", resumeID: "s", anchor: nil, base: base)
        broken.finish(.failure(SessionHistoryError.unreadable), generation: broken.generation)
        #expect(broken.phase == .failed && broken.canLoad)
    }

    @Test func edgesAreRoutedOnceAndCulledToTheViewport() {
        let layout = MightyGraphLayout.make(runs: (0..<6).map { run("r\($0)") }, draft: "", running: false, expanded: [])
        #expect(layout.routes() == layout.edges.map(layout.route))
        let first = layout.nodes.map(\.frame).reduce(CGRect.null) { $0.union($1) }
        let near = layout.routes(in: CGRect(x: first.minX, y: first.minY, width: first.width, height: 200))
        #expect(!near.isEmpty && near.count < layout.edges.count)
        #expect(layout.routes(in: CGRect(x: -10_000, y: -10_000, width: 10, height: 10)).isEmpty)
    }
}
