import Foundation
import Testing
@testable import MightyCore

/// Synthetic `$CODEX_HOME/sessions` fixtures shaped like multi-agent v2
/// rollout files: a forked child replays its parent's history before
/// `subagent_history_start_ordinal`, and gets its task as an `agent_message`
/// whose only readable part is a labelled header (the body is encrypted).
struct CodexSessionRecordTests {
    static let root = "0190aaaa-0000-7000-8000-000000000001"
    static let child = "0190aaaa-0000-7000-8000-000000000002"
    static let grandchild = "0190aaaa-0000-7000-8000-000000000003"
    static let stranger = "0190aaaa-0000-7000-8000-000000000004"
    static let guardian = "0190aaaa-0000-7000-8000-000000000005"

    final class Home {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-codex-home-" + UUID().uuidString, isDirectory: true)
        let started = Date().addingTimeInterval(-60)
        var today: URL {
            var calendar = Calendar(identifier: .gregorian); calendar.timeZone = .current
            let parts = calendar.dateComponents([.year, .month, .day], from: Date())
            return url.appendingPathComponent(String(format: "sessions/%04d/%02d/%02d", parts.year!, parts.month!, parts.day!), isDirectory: true)
        }
        init(sessionsLink: Bool = false) throws {
            if sessionsLink {
                // `sessions` is a link to a real folder elsewhere.
                let real = url.appendingPathComponent("real-sessions", isDirectory: true)
                try FileManager.default.createDirectory(at: real, withIntermediateDirectories: true)
                try FileManager.default.createSymbolicLink(at: url.appendingPathComponent("sessions"), withDestinationURL: real)
            }
            try FileManager.default.createDirectory(at: today, withIntermediateDirectories: true)
        }
        deinit { try? FileManager.default.removeItem(at: url) }
        func file(_ thread: String, _ stamp: String = "10-00-00") -> URL { today.appendingPathComponent("rollout-2026-01-01T\(stamp)-\(thread).jsonl") }
        func write(_ thread: String, _ lines: [String], stamp: String = "10-00-00") throws {
            try Data(lines.map { $0 + "\n" }.joined().utf8).write(to: file(thread, stamp))
        }
        func append(_ thread: String, _ text: String, stamp: String = "10-00-00") throws {
            let handle = try FileHandle(forWritingTo: file(thread, stamp))
            try handle.seekToEnd(); try handle.write(contentsOf: Data(text.utf8)); try handle.close()
        }
        func watcher(limits: CodexSessionWatcher.Limits = .init()) -> CodexSessionWatcher {
            CodexSessionWatcher(codexHome: url, rootThread: CodexSessionRecordTests.root, startedAt: started, namespace: "run", limits: limits)
        }
    }

    static func json(_ value: [String: Any]) -> String {
        String(decoding: try! JSONSerialization.data(withJSONObject: value, options: [.sortedKeys]), as: UTF8.self)
    }
    /// Codex's fixed-width UTC form.
    static func stamp(_ date: Date = Date()) -> String {
        let formatter = ISO8601DateFormatter(); formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: date)
    }
    /// A record in Codex's key order: timestamp, ordinal, type, payload.
    static func record(_ type: String, _ payload: [String: Any], ordinal: Int? = nil, metadata: [String: Any]? = nil, at date: Date = Date()) -> String {
        var value: [String: Any] = ["timestamp": stamp(date), "type": type, "payload": payload]
        if let ordinal { value["ordinal"] = ordinal }
        if let metadata { value["metadata"] = metadata }
        // Sorted keys would move `timestamp` and `ordinal`; put them first.
        let rest = json(value.filter { $0.key != "timestamp" && $0.key != "ordinal" }).dropFirst()
        return "{\"timestamp\":\"" + stamp(date) + "\"," + (ordinal.map { "\"ordinal\":\($0)," } ?? "") + rest
    }
    static func meta(_ thread: String, parent: String?, nickname: String? = nil, path: String? = nil, start: Int? = nil, forked: Bool = false, spawn: Bool = true) -> String {
        var payload: [String: Any] = ["id": thread, "cwd": "/tmp/project", "base_instructions": ["text": "Base instructions"]]
        if let parent {
            payload["parent_thread_id"] = parent; payload["thread_source"] = "subagent"
            payload["source"] = ["subagent": spawn ? ["thread_spawn": ["parent_thread_id": parent, "depth": 1, "agent_path": path.map { $0 as Any } ?? NSNull(), "agent_nickname": nickname.map { $0 as Any } ?? NSNull()]] : ["other": "guardian"]]
        } else { payload["source"] = "exec" }
        if let nickname { payload["agent_nickname"] = nickname }
        if let path { payload["agent_path"] = path }
        if let start { payload["subagent_history_start_ordinal"] = start }
        if start != nil || forked { payload["forked_from_id"] = parent }
        return record("session_meta", payload, ordinal: 0)
    }
    static func event(_ type: String, _ payload: [String: Any] = [:], ordinal: Int? = nil, at date: Date = Date()) -> String {
        var value = payload; value["type"] = type
        return record("event_msg", value, ordinal: ordinal, at: date)
    }
    static func item(_ item: [String: Any], ordinal: Int? = nil, at date: Date = Date()) -> String {
        event("item_completed", ["thread_id": child, "turn_id": "turn", "item": item, "started_at_ms": 1_000, "completed_at_ms": 1_250], ordinal: ordinal, at: date)
    }
    static func command(_ id: String, _ line: String, ordinal: Int? = nil, at date: Date = Date()) -> String {
        item(["type": "CommandExecution", "id": id, "command": ["/bin/zsh", "-lc", line], "status": "completed", "aggregated_output": "", "exit_code": 0], ordinal: ordinal, at: date)
    }
    static func usage(_ response: String, ordinal: Int? = nil, at date: Date = Date()) -> String {
        record("token_usage_record", ["response_id": response, "usage": ["input_tokens": 100, "output_tokens": 10, "cached_input_tokens": 50]], ordinal: ordinal, at: date)
    }
    /// A v2 task: a readable header of labelled lines (the last one empty)
    /// followed by the encrypted body. The header is synthetic.
    static func task(to path: String, id: String, ordinal: Int? = nil) -> String {
        let header = "Message Type: sample\nTask name: \(path)\nSender: /root\nPayload:\n"
        return record("response_item", ["type": "agent_message", "id": id, "author": "/root", "recipient": path,
                                        "content": [["type": "input_text", "text": header], ["type": "encrypted_content", "encrypted_content": "c2VhbGVk"]]], ordinal: ordinal)
    }

    /// Forked history (ordinals 1-3) must never reach the child's block.
    static var childLines: [String] {
        [meta(child, parent: root, nickname: "Ada", path: "/root/scan", start: 4),
         record("session_meta", ["id": root, "source": "exec"], ordinal: 1),
         record("response_item", ["type": "message", "role": "user", "content": [["type": "input_text", "text": "Parent request"]]], ordinal: 2, metadata: ["inherited_user_message": true]),
         event("task_started", ["turn_id": "parent-turn"], ordinal: 3),
         event("thread_settings_applied", ["thread_id": child], ordinal: 4),
         event("task_started", ["turn_id": "turn"], ordinal: 5),
         record("response_item", ["type": "message", "role": "developer", "content": [["type": "input_text", "text": "Role instructions"]]], ordinal: 6),
         record("turn_context", ["turn_id": "turn", "model": "gpt-test"], ordinal: 7),
         task(to: "/root/scan", id: "task-1", ordinal: 8),
         item(["type": "CommandExecution", "id": "call-1", "command": ["/bin/zsh", "-lc", "ls"], "status": "completed", "aggregated_output": "a.txt", "exit_code": 0], ordinal: 9),
         usage("resp-1", ordinal: 10),
         item(["type": "AgentMessage", "id": "msg-1", "phase": "commentary", "content": [["type": "Text", "text": "Listing files"]]], ordinal: 11),
         item(["type": "CommandExecution", "id": "call-2", "command": ["/bin/zsh", "-lc", "false"], "status": "failed", "aggregated_output": "", "exit_code": 1], ordinal: 12),
         item(["type": "AgentMessage", "id": "msg-2", "phase": "final_answer", "content": [["type": "Text", "text": "Found a.txt"]]], ordinal: 13)]
    }
    static let childDone = event("task_complete", ["turn_id": "turn", "last_agent_message": "Found a.txt"], ordinal: 14)
    /// An older, unforked child that gets its task as a plain user message.
    static var grandchildLines: [String] {
        [meta(grandchild, parent: child, nickname: "Bo", path: "/root/scan/deep"),
         event("task_started", ["turn_id": "deep"]),
         record("response_item", ["type": "message", "role": "user", "content": [["type": "input_text", "text": "Look deeper"]]]),
         event("turn_aborted", ["turn_id": "deep", "reason": "interrupted"])]
    }

    @Test func discoversChildrenByParentAndMapsTheirRecords() throws {
        let home = try Home()
        try home.write(Self.root, [Self.meta(Self.root, parent: nil)], stamp: "09-59-59")
        try home.write(Self.child, Self.childLines + [Self.childDone])
        try home.write(Self.grandchild, Self.grandchildLines, stamp: "10-00-01")
        try home.write(Self.stranger, [Self.meta(Self.stranger, parent: "0190aaaa-0000-7000-8000-00000000ffff", nickname: "Eve", path: "/root/x")], stamp: "10-00-02")
        try home.write(Self.guardian, [Self.meta(Self.guardian, parent: Self.root, spawn: false)], stamp: "10-00-03")
        let agents = home.watcher().poll()
        #expect(agents.map(\.thread) == [Self.child, Self.grandchild])
        let child = try #require(agents.first)
        #expect(child.parentThread == Self.root)
        #expect(child.title == "Ada · scan")
        #expect(child.input == nil)
        #expect(child.state == "completed")
        #expect(child.output == "Found a.txt")
        #expect(child.turns == 1)
        #expect(child.entries.map(\.kind) == ["system", "assistant", "system"])
        #expect(!child.entries.contains { $0.text.contains("Parent request") || $0.text.contains("Role instructions") })
        let command = try #require(child.entries.first?.activity)
        #expect(command.kind == "command" && command.state == "completed" && command.summary.contains("ls"))
        #expect(command.output == "a.txt" && command.durationMs == 250)
        #expect(child.entries.last?.activity?.state == "error")
        #expect(child.usage.map(\.responseId) == ["resp-1"])
        #expect(child.usage.first?.model == "gpt-test")
        #expect(child.usage.first?.usage == GraphTokenUsage(inputTokens: 100, outputTokens: 10, cacheReadTokens: 50))
        let grandchild = try #require(agents.last)
        #expect(grandchild.parentThread == Self.child)
        #expect(grandchild.title == "Bo · deep")
        #expect(grandchild.input == "Look deeper")
        #expect(grandchild.state == "stopped")
    }

    @Test func tailConsumesOnlyCompleteLinesAndSkipsOversizedOnes() throws {
        let home = try Home()
        let url = home.today.appendingPathComponent("tail.jsonl")
        try Data("one\ntw".utf8).write(to: url)
        var tail = CodexRolloutTail(maximumLineBytes: 16)
        func read() throws -> [String] {
            let fd = try #require(CodexSessionFiles.open(url)); defer { close(fd) }
            let result = tail.read(fd, maximumBytes: 1_024)
            return try #require(result).lines.map { String(decoding: $0, as: UTF8.self) }
        }
        #expect(try read() == ["one"])
        #expect(tail.offset == 4)
        #expect(try read().isEmpty)
        let handle = try FileHandle(forWritingTo: url); try handle.seekToEnd()
        try handle.write(contentsOf: Data("o\n".utf8))
        #expect(try read() == ["two"])
        try handle.write(contentsOf: Data((String(repeating: "x", count: 40) + "\nthree\n" + String(repeating: "y", count: 20)).utf8))
        #expect(try read() == ["three"])
        try handle.write(contentsOf: Data("\nfour\n".utf8)); try handle.close()
        #expect(try read() == ["four"])
    }

    @Test func partialLineWaitsAndFileAppearingMidRunIsFound() throws {
        let home = try Home()
        let watcher = home.watcher()
        #expect(watcher.poll().isEmpty)
        let done = Self.childDone
        try home.write(Self.child, Self.childLines)
        try home.append(Self.child, String(done.prefix(20)))
        var agents = watcher.poll()
        #expect(agents.map(\.state) == ["running"])
        #expect(agents.first?.output == "Found a.txt")
        #expect(watcher.poll().isEmpty)
        try home.append(Self.child, String(done.dropFirst(20)) + "\n")
        agents = watcher.poll()
        #expect(agents.map(\.state) == ["completed"])
        #expect(agents.first?.usage.isEmpty == true)
        try home.write(Self.grandchild, Self.grandchildLines, stamp: "10-00-01")
        #expect(watcher.poll().map(\.thread) == [Self.grandchild])
    }

    @Test func garbageOversizedSymlinkedAndOldFilesAreIgnored() throws {
        let home = try Home()
        var limits = CodexSessionWatcher.Limits(); limits.maximumLineBytes = 1_024
        let big = Self.item(["type": "AgentMessage", "id": "huge", "phase": "commentary", "content": [["type": "Text", "text": String(repeating: "z", count: 4_000)]]], ordinal: 9)
        let lines = Array(Self.childLines.prefix(9)) + ["not json", "{\"type\":", big, "[1,2]"] + [Self.childDone]
        try home.write(Self.child, lines)
        // A link to a child file elsewhere is never followed.
        let outside = home.url.appendingPathComponent("elsewhere.jsonl")
        try Data((Self.grandchildLines.joined(separator: "\n") + "\n").utf8).write(to: outside)
        try FileManager.default.createSymbolicLink(at: home.file(Self.grandchild, "10-00-01"), withDestinationURL: outside)
        // A child record last written before the run is not this run's.
        try home.write(Self.stranger, [Self.meta(Self.stranger, parent: Self.root, nickname: "Old", path: "/root/old")], stamp: "08-00-00")
        try FileManager.default.setAttributes([.modificationDate: Date().addingTimeInterval(-3_600)], ofItemAtPath: home.file(Self.stranger, "08-00-00").path)
        try Data("garbage\n".utf8).write(to: home.file("0190aaaa-0000-7000-8000-000000000006", "10-00-05"))
        try Data("x".utf8).write(to: home.today.appendingPathComponent("rollout-not-a-thread.jsonl"))
        let agents = home.watcher(limits: limits).poll()
        #expect(agents.map(\.thread) == [Self.child])
        #expect(agents.first?.state == "completed")
        #expect(agents.first?.input == nil)
        #expect(agents.first?.entries.isEmpty == true)
    }

    @Test func sessionChildrenBecomeBlocksUnderTheirParents() throws {
        let home = try Home()
        try home.write(Self.child, Self.childLines + [Self.childDone])
        try home.write(Self.grandchild, Self.grandchildLines, stamp: "10-00-01")
        var session = RunSession(id: "session", workspaceId: "workspace", title: "Codex", provider: "codex")
        session.beginGraphRun(input: "Review", id: "request", configuredModel: "default")
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in }, activityNamespace: "run", graph: {
            nodes.append($0); session.recordGraph(RunEvent(sessionId: "session", type: "graph", graph: $0))
        })
        parser.push(Self.json(["type": "thread.started", "thread_id": Self.root]) + "\n")
        #expect(parser.codexRootThread == Self.root)
        // A grandchild seen before its parent still lands under it.
        let agents = home.watcher().poll()
        parser.receiveCodexSessions(Array(agents.reversed()))
        let childID = ExecutionGraphSupport.identifier("run", "codex-agent:" + Self.child)
        let grandchildID = ExecutionGraphSupport.identifier("run", "codex-agent:" + Self.grandchild)
        let child = try #require(nodes.last { $0.id == childID })
        #expect(child.parentId == ExecutionGraphSupport.mainNodeID(runId: "run"))
        #expect(child.title == "Ada · scan" && child.input == nil && child.output == "Found a.txt")
        #expect(child.state == "completed")
        #expect(child.usage == GraphTokenUsage(inputTokens: 100, outputTokens: 10, cacheReadTokens: 50))
        #expect(child.responseRecords?.first?.model == "gpt-test")
        #expect(try #require(nodes.last { $0.id == grandchildID }).parentId == childID)
        let graph = try #require(session.mightyGraphRuns.first)
        #expect(graph.agents.count == 2)
        #expect(graph.agents.first { $0.id == grandchildID }?.parentID == childID)
        let block = try #require(graph.agents.first { $0.id == childID })
        #expect(block.entries.filter { $0.text == "Found a.txt" }.count == 1)
        #expect(block.entries.contains { $0.activity?.kind == "command" })
        // Re-reading the same snapshot neither duplicates nor re-counts.
        let emitted = nodes.count
        parser.receiveCodexSessions(agents)
        #expect(nodes.count == emitted)
        parser.finishGraph(state: "completed")
        #expect(nodes.last { $0.id == grandchildID }?.state == "stopped")
        #expect(nodes.last { $0.id == childID }?.state == "completed")
    }

    @Test func legacyCollabItemAndSessionRecordShareOneBlock() throws {
        let home = try Home()
        try home.write(Self.child, Self.childLines)
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in }, activityNamespace: "run", graph: { nodes.append($0) })
        parser.push(Self.json(["type": "thread.started", "thread_id": Self.root]) + "\n")
        parser.push(Self.json(["type": "item.completed", "item": ["id": "spawn", "type": "collab_tool_call", "tool": "spawn_agent", "sender_thread_id": Self.root,
            "receiver_thread_ids": [Self.child], "agents_states": [Self.child: ["status": "running"]], "prompt": "Scan the repository", "status": "completed"]]) + "\n")
        parser.receiveCodexSessions(home.watcher().poll())
        #expect(Set(nodes.filter { $0.kind == "agent" }.map(\.id)) == [ExecutionGraphSupport.identifier("run", "codex-agent:" + Self.child)])
        let block = try #require(nodes.last)
        #expect(block.title == "Ada · scan")
        #expect(block.input == "Scan the repository")
        #expect(block.entries.count == 3)
        #expect(block.state == "running")
    }

    @Test func aNewTurnOnASettledChildReopensItOnce() throws {
        let home = try Home()
        try home.write(Self.child, Self.childLines + [Self.childDone])
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in }, activityNamespace: "run", graph: { nodes.append($0) })
        parser.push(Self.json(["type": "thread.started", "thread_id": Self.root]) + "\n")
        let watcher = home.watcher()
        parser.receiveCodexSessions(watcher.poll())
        try home.append(Self.child, [Self.event("task_started", ["turn_id": "turn-2"], ordinal: 15),
                                     Self.task(to: "/root/scan", id: "task-3", ordinal: 16)].joined(separator: "\n") + "\n")
        parser.receiveCodexSessions(watcher.poll())
        var block = try #require(nodes.last)
        #expect(block.state == "running" && block.activityGeneration == 1 && block.output == nil)
        // The first answer is kept once; the sealed follow-up adds no entry.
        #expect(block.entries.filter { $0.kind == "assistant" && $0.text == "Found a.txt" }.count == 1)
        #expect(!block.entries.contains { $0.kind == "user" })
        try home.append(Self.child, Self.event("task_complete", ["turn_id": "turn-2", "last_agent_message": "No b.txt"], ordinal: 17) + "\n")
        parser.receiveCodexSessions(watcher.poll())
        block = try #require(nodes.last)
        #expect(block.state == "completed" && block.activityGeneration == 1 && block.output == "No b.txt")
    }

    @Test func watcherStopsAfterTheRunEnds() async throws {
        let home = try Home()
        try home.write(Self.child, Self.childLines)
        let watcher = home.watcher()
        var sleeps = 0; var polls = 0
        await CodexSessionWatcher.drive(sleep: { sleeps += 1 }) {
            polls += 1
            _ = watcher.poll()
            return polls < 3
        }
        #expect(sleeps == 3 && polls == 3)
        try home.append(Self.child, Self.childDone + "\n")
        #expect(watcher.finish().map(\.state) == ["completed"])
        #expect(watcher.stopped)
        try home.write(Self.grandchild, Self.grandchildLines, stamp: "10-00-01")
        #expect(watcher.poll().isEmpty)
        #expect(watcher.finish().isEmpty)
        // A cancelled sleep ends the loop without another poll.
        polls = 0
        await CodexSessionWatcher.drive(sleep: { throw CancellationError() }) { polls += 1; return true }
        #expect(polls == 0)
    }

    @Test func prefixIsReadWithoutParsing() {
        let line = Data(Self.record("event_msg", ["type": "task_started"], ordinal: 42).utf8)
        let prefix = CodexSessionThread.prefix(line)
        #expect(prefix.ordinal == 42 && prefix.timestamp?.hasSuffix("Z") == true)
        #expect(CodexSessionThread.prefix(Data("{\"type\":\"x\"}".utf8)).timestamp == nil)
        #expect(CodexSessionThread.prefix(Data(Self.record("event_msg", ["type": "task_started"]).utf8)).ordinal == nil)
    }

    @Test func sealedV2MessagesAddNoInputOrFollowUp() throws {
        let home = try Home()
        let readable = Self.record("response_item", ["type": "agent_message", "id": "task-9", "author": "/root", "recipient": "/root/scan",
                                                     "content": [["type": "input_text", "text": "Readable follow-up"]]], ordinal: 21)
        try home.write(Self.child, Self.childLines + [Self.childDone, Self.event("task_started", ["turn_id": "turn-2"], ordinal: 15),
                                                     Self.task(to: "/root/scan", id: "task-3", ordinal: 16)])
        let watcher = home.watcher()
        let agent = try #require(watcher.poll().first)
        #expect(CodexSessionThread.envelopeHeader("Message Type: sample\nTask name: /root/scan\nSender: /root\nPayload:\n"))
        #expect(!CodexSessionThread.envelopeHeader("Please check: a.txt\n"))
        #expect(!CodexSessionThread.envelopeHeader("Goal: find a.txt\nNote: then stop\n"))
        #expect(agent.input == nil)
        #expect(!agent.entries.contains { $0.kind == "user" })
        // Plain text addressed to the child is still its request.
        try home.append(Self.child, readable + "\n")
        #expect(watcher.poll().first?.input == "Readable follow-up")
    }

    @Test func reusedChildShowsOnlyThisRunsTurn() throws {
        let home = try Home()
        let old = home.started.addingTimeInterval(-3_600)
        // A resumed root brings back a child from an earlier run.
        try home.write(Self.child, [Self.meta(Self.child, parent: Self.root, nickname: "Ada", path: "/root/scan", start: 1),
            Self.event("task_started", ["turn_id": "old"], ordinal: 1, at: old),
            Self.command("old-call", "ls old", ordinal: 2, at: old),
            Self.usage("resp-old", ordinal: 3, at: old),
            Self.event("task_complete", ["turn_id": "old", "last_agent_message": "Old answer"], ordinal: 4, at: old),
            Self.event("task_started", ["turn_id": "new"], ordinal: 5),
            Self.command("new-call", "ls new", ordinal: 6),
            Self.usage("resp-new", ordinal: 7),
            Self.event("task_complete", ["turn_id": "new", "last_agent_message": "New answer"], ordinal: 8)])
        // Another earlier child written again during this run, with no new turn.
        try home.write(Self.grandchild, [Self.meta(Self.grandchild, parent: Self.root, nickname: "Bo", path: "/root/idle"),
            Self.event("task_started", ["turn_id": "old"], at: old),
            Self.event("task_complete", ["turn_id": "old", "last_agent_message": "Old idle answer"], at: old)], stamp: "10-00-01")
        let agents = home.watcher().poll()
        #expect(agents.map(\.thread) == [Self.child])
        let agent = try #require(agents.first)
        #expect(agent.turns == 1 && agent.state == "completed" && agent.output == "New answer")
        #expect(agent.usage.map(\.responseId) == ["resp-new"])
        #expect(agent.entries.count == 1 && agent.entries.first?.activity?.summary.contains("ls new") == true)
        #expect(!agent.entries.contains { $0.text.contains("Old answer") })
    }

    @Test func twoTurnsInOneReadKeepTheFirstAnswer() throws {
        let home = try Home()
        try home.write(Self.child, Self.childLines + [Self.childDone,
            Self.event("task_started", ["turn_id": "turn-2"], ordinal: 15),
            Self.event("task_complete", ["turn_id": "turn-2", "last_agent_message": "Second answer"], ordinal: 16)])
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in }, activityNamespace: "run", graph: { nodes.append($0) })
        parser.push(Self.json(["type": "thread.started", "thread_id": Self.root]) + "\n")
        let agents = home.watcher().poll()
        let agent = try #require(agents.first)
        #expect(agent.turns == 2 && agent.output == "Second answer")
        #expect(agent.entries.filter { $0.kind == "assistant" }.map(\.text) == ["Listing files", "Found a.txt"])
        parser.receiveCodexSessions(agents)
        let block = try #require(nodes.last)
        #expect(block.state == "completed" && block.output == "Second answer")
        #expect(block.entries.filter { $0.text == "Found a.txt" }.count == 1)
    }

    @Test func trimmedEntriesAreNotAddedBackAsNewest() throws {
        let home = try Home()
        let big = String(repeating: "w", count: 8_000)
        let notes = (0..<10).map { Self.item(["type": "AgentMessage", "id": "note-\($0)", "phase": "commentary", "content": [["type": "Text", "text": "\($0) " + big]]], ordinal: 20 + $0) }
        try home.write(Self.child, Array(Self.childLines.prefix(9)) + notes)
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in }, activityNamespace: "run", graph: { nodes.append($0) })
        parser.push(Self.json(["type": "thread.started", "thread_id": Self.root]) + "\n")
        let watcher = home.watcher()
        parser.receiveCodexSessions(watcher.poll())
        func order() throws -> [Int] {
            try #require(nodes.last).entries.compactMap { Int($0.text.prefix { $0 != " " }) }
        }
        let first = try order()
        #expect(first.count < 10 && first.last == 9 && first == first.sorted())
        try home.append(Self.child, Self.item(["type": "AgentMessage", "id": "note-10", "phase": "commentary", "content": [["type": "Text", "text": "10 " + big]]], ordinal: 30) + "\n")
        parser.receiveCodexSessions(watcher.poll())
        let second = try order()
        #expect(second.last == 10 && second == second.sorted() && second.first ?? 0 >= first.first ?? 0)
    }

    @Test func errorThenCompletionDoesNotDependOnPollTiming() throws {
        let error = Self.event("error", ["message": "stream disconnected"], ordinal: 14)
        let done = Self.event("task_complete", ["turn_id": "turn", "last_agent_message": "Found a.txt"], ordinal: 15)
        func states(split: Bool) throws -> String? {
            let home = try Home()
            try home.write(Self.child, Self.childLines + [error] + (split ? [] : [done]))
            var nodes: [ExecutionGraphNode] = []
            let parser = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in }, activityNamespace: "run", graph: { nodes.append($0) })
            parser.push(Self.json(["type": "thread.started", "thread_id": Self.root]) + "\n")
            let watcher = home.watcher()
            parser.receiveCodexSessions(watcher.poll())
            if split { try home.append(Self.child, done + "\n"); parser.receiveCodexSessions(watcher.poll()) }
            #expect(nodes.last?.entries.contains { $0.kind == "error" } == true)
            return nodes.last?.state
        }
        #expect(try states(split: true) == "completed")
        #expect(try states(split: false) == "completed")
        // A turn that reported an error and never completed failed.
        let home = try Home()
        try home.write(Self.child, Self.childLines + [error])
        let watcher = home.watcher()
        #expect(watcher.poll().first?.state == "running")
        #expect(watcher.finish().first?.state == "error")
    }

    @Test func forkWithoutOrdinalsShowsOnlyItsState() throws {
        let home = try Home()
        try home.write(Self.child, [Self.meta(Self.child, parent: Self.root, nickname: "Ada", path: "/root/scan", forked: true),
            Self.event("task_started", ["turn_id": "parent"]),
            Self.command("parent-call", "ls parent"),
            Self.event("thread_settings_applied", ["thread_id": Self.child]),
            Self.event("task_started", ["turn_id": "turn"]),
            Self.command("call-1", "ls"),
            Self.usage("resp-1"),
            Self.event("error", ["message": "failed once"]),
            Self.event("task_complete", ["turn_id": "turn", "last_agent_message": "Found a.txt"])])
        let agent = try #require(home.watcher().poll().first)
        #expect(agent.state == "completed" && agent.turns == 1)
        #expect(agent.entries.isEmpty && agent.usage.isEmpty && agent.output == nil && agent.input == nil)
    }

    @Test func waitingFilesAreEvictedOldestFirstAndAdoptedWithTheirParent() throws {
        let home = try Home()
        var limits = CodexSessionWatcher.Limits(); limits.maximumWaiting = 2
        let watcher = home.watcher(limits: limits)
        let strangers = ["0190aaaa-0000-7000-8000-00000000000a", "0190aaaa-0000-7000-8000-00000000000b", "0190aaaa-0000-7000-8000-00000000000c"]
        for (index, stranger) in strangers.enumerated() {
            try home.write(stranger, [Self.meta(stranger, parent: "0190aaaa-0000-7000-8000-00000000ffff", path: "/root/x"), Self.event("task_started")], stamp: "09-00-0\(index)")
            #expect(watcher.poll().isEmpty)
        }
        #expect(watcher.firstLineReads == 3)
        // Evicted and waiting files are not read again on the next poll.
        #expect(watcher.poll().isEmpty && watcher.firstLineReads == 3)
        // A grandchild seen before its parent waits (evicting the oldest)...
        try home.write(Self.grandchild, Self.grandchildLines, stamp: "10-00-01")
        #expect(watcher.poll().isEmpty && watcher.firstLineReads == 4)
        // ...and is adopted, re-read once, when its parent arrives.
        try home.write(Self.child, Self.childLines + [Self.childDone])
        #expect(watcher.poll().map(\.thread).sorted() == [Self.child, Self.grandchild])
        #expect(watcher.firstLineReads == 6)
    }

    @Test func truncatedFileStopsBeingRead() throws {
        let home = try Home()
        try home.write(Self.child, Self.childLines)
        let watcher = home.watcher()
        #expect(watcher.poll().first?.state == "running")
        try home.write(Self.child, [Self.meta(Self.child, parent: Self.root, nickname: "Ada", path: "/root/scan", start: 4)])
        try home.append(Self.child, Self.childDone + "\n")
        #expect(watcher.poll().isEmpty)
        try home.append(Self.child, Self.childDone + "\n" + Self.childDone + "\n" + Self.childDone + "\n")
        #expect(watcher.poll().isEmpty)
        #expect(watcher.finish().isEmpty)
    }

    @Test func linkedSessionsFolderIsFollowedButLinkedDayFoldersAreNot() throws {
        let home = try Home(sessionsLink: true)
        try home.write(Self.child, Self.childLines + [Self.childDone])
        // Yesterday's folder is a link to a folder holding another child.
        var calendar = Calendar(identifier: .gregorian); calendar.timeZone = .current
        let parts = calendar.dateComponents([.year, .month, .day], from: calendar.date(byAdding: .day, value: -1, to: Date())!)
        let elsewhere = home.url.appendingPathComponent("elsewhere", isDirectory: true)
        try FileManager.default.createDirectory(at: elsewhere, withIntermediateDirectories: true)
        try Data((Self.grandchildLines.joined(separator: "\n") + "\n").utf8).write(to: elsewhere.appendingPathComponent("rollout-2026-01-01T10-00-01-\(Self.grandchild).jsonl"))
        let yesterday = home.url.appendingPathComponent(String(format: "sessions/%04d/%02d/%02d", parts.year!, parts.month!, parts.day!))
        try? FileManager.default.createDirectory(at: yesterday.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: yesterday, withDestinationURL: elsewhere)
        let watcher = CodexSessionWatcher(codexHome: home.url, rootThread: Self.root, startedAt: home.started, namespace: "run")
        #expect(watcher.poll().map(\.thread) == [Self.child])
    }
}
