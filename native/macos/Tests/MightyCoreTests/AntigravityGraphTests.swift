import Darwin
import Foundation
import SQLite3
import Testing
@testable import MightyCore

/// The Mac's Antigravity panes in the Mighty views: the live graph from agy's
/// stream and its sub-agents' own transcripts, older requests read back from
/// the conversation's transcript, the conversations to continue and the
/// skills. Every id, path and text is made up; the shapes follow what agy
/// 1.3.5 printed and wrote for a small run that started one sub-agent.
@Suite(.serialized)
struct AntigravityGraphTests {
    static let parent = "aaaaaaaa-0000-4000-8000-000000000001"
    static let child = "aaaaaaaa-0000-4000-8000-000000000002"
    static let grandchild = "aaaaaaaa-0000-4000-8000-000000000003"

    private static func json(_ object: [String: Any]) -> String {
        String(decoding: try! JSONSerialization.data(withJSONObject: object, options: [.sortedKeys]), as: UTF8.self)
    }
    private static func step(_ fields: [String: Any]) -> String { json(["event": "step_update", "step_update": fields.merging(["conversation_id": parent]) { a, _ in a }]) }
    private static let spec: [String: Any] = ["type_name": "research", "role": "File Reader", "initial_prompt": "Read a.txt", "conversation_id": child,
                                              "log_uri": "file:///home/demo/.gemini/antigravity-cli/brain/\(child)/.system_generated/logs/transcript.jsonl", "workspace_uris": ["file:///workspace/demo"]]

    /// The stream of a run that starts one sub-agent and answers after its message.
    static var subagentStream: String {
        [json(["event": "init", "conversation_id": parent, "init": ["model": "gemini-3.8-flash-low", "cwd": "/workspace/demo", "permission_mode": "request-review"]]),
         step(["step_index": 0, "state": "DONE", "step_type": "user_input"]),
         step(["step_index": 1, "state": "DONE", "step_type": "agent_response", "usage": ["input_tokens": 100, "output_tokens": 10, "thinking_tokens": 0, "cache_read_tokens": 0]]),
         step(["step_index": 2, "state": "ACTIVE", "step_type": "subagent", "tool_name": "invoke_subagent", "subagent_info": ["subagents": [spec]]]),
         step(["step_index": 2, "state": "DONE", "step_type": "subagent", "tool_name": "invoke_subagent", "subagent_info": ["subagents": [spec]]]),
         step(["step_index": 3, "state": "ACTIVE", "step_type": "agent_response", "text_delta": "Waiting for "]),
         step(["step_index": 3, "state": "DONE", "step_type": "agent_response", "text_delta": "the helper.\n", "usage": ["input_tokens": 120, "output_tokens": 5, "cache_read_tokens": 0]]),
         step(["step_index": 4, "state": "DONE", "step_type": "system_message"]),
         step(["step_index": 5, "state": "DONE", "step_type": "agent_response", "text_delta": "The file says hi.\n", "usage": ["input_tokens": 130, "output_tokens": 6, "cache_read_tokens": 40]]),
         json(["event": "result", "result": ["conversation_id": parent, "status": "SUCCESS", "response": "Waiting for the helper.\nThe file says hi.\n", "num_turns": 1]])]
            .joined(separator: "\n") + "\n"
    }

    // MARK: Transcripts

    private static func line(_ index: Int, _ source: String, _ type: String, status: String = "DONE", minute: Int = 0, _ extra: [String: Any] = [:]) -> String {
        json(["step_index": index, "source": source, "type": type, "status": status, "created_at": String(format: "2026-01-02T03:%02d:00Z", minute)].merging(extra) { a, _ in a })
    }
    private static func request(_ text: String) -> String {
        "<USER_REQUEST>\n\(text)\n</USER_REQUEST>\n<ADDITIONAL_METADATA>\nThe current local time is: 2026-01-02T12:00:00+09:00.\n</ADDITIONAL_METADATA>"
    }
    private static func message(from sender: String, _ text: String) -> String {
        "The following is a <SYSTEM_MESSAGE> not actually sent by the user.\n\n<SYSTEM_MESSAGE>\n[Message] timestamp=2026-01-02T03:00:00Z sender=\(sender) priority=MESSAGE_PRIORITY_HIGH content=\(text)\n</SYSTEM_MESSAGE>"
    }
    private static func call(_ name: String, _ args: [String: Any]) -> [String: Any] {
        ["name": name, "args": args.merging(["toolAction": "Doing it", "toolSummary": "Summary of " + name]) { a, _ in a }]
    }
    private static func spawned(_ id: String) -> String {
        "Created At: 2026-01-02T12:00:00+09:00\nCompleted At: 2026-01-02T12:00:00+09:00\nCreated the following subagents:\n{\n  \"conversationId\": \"\(id)\",\n  \"workspaceUris\": [\n    \"file:///workspace/demo\"\n  ]\n}\nThe subagents will send you a message when they have completed their task."
    }

    /// The sub-agent: reads a file, starts its own helper, sends its answer back.
    static let childLines = [
        line(0, "SYSTEM", "SYSTEM_MESSAGE", minute: 1, ["content": message(from: parent, "Read a.txt")]),
        line(1, "MODEL", "PLANNER_RESPONSE", minute: 1, ["tool_calls": [call("view_file", ["AbsolutePath": "/workspace/demo/a.txt"])], "input_tokens": 50, "output_tokens": 5, "cache_read_tokens": 0]),
        line(2, "MODEL", "GENERIC", minute: 1, ["content": "Created At: x\nCompleted At: x\nFile Path: `file:///workspace/demo/a.txt`\n1: secret file line"]),
        line(3, "MODEL", "PLANNER_RESPONSE", minute: 2, ["tool_calls": [call("invoke_subagent", ["Subagents": [["Role": "Checker", "TypeName": "research", "Prompt": "Check it", "Model": "inherit"]]])]]),
        line(4, "MODEL", "GENERIC", minute: 2, ["content": spawned(grandchild)]),
        line(5, "SYSTEM", "SYSTEM_MESSAGE", minute: 2, ["content": message(from: grandchild, "Checked.")]),
        line(6, "MODEL", "PLANNER_RESPONSE", minute: 2, ["tool_calls": [call("send_message", ["Recipient": parent, "Message": "It says hi"])], "input_tokens": 60, "output_tokens": 7, "cache_read_tokens": 10]),
        line(7, "MODEL", "GENERIC", minute: 2, ["content": "Created At: x\nCompleted At: x\nMessage sent."]),
        line(8, "MODEL", "PLANNER_RESPONSE", minute: 3, ["content": "Sent the content back."]),
    ]
    static let grandchildLines = [
        line(0, "SYSTEM", "SYSTEM_MESSAGE", minute: 2, ["content": message(from: child, "Check it")]),
        line(1, "MODEL", "PLANNER_RESPONSE", minute: 2, ["tool_calls": [call("send_message", ["Recipient": child, "Message": "Checked."])]]),
        line(2, "MODEL", "GENERIC", minute: 2, ["content": "Message sent."]),
        line(3, "MODEL", "PLANNER_RESPONSE", minute: 2, ["content": "Done checking."]),
    ]
    /// Three requests: a command, a sub-agent, and one cut short by a declined edit.
    static let parentLines = [
        line(0, "USER_EXPLICIT", "USER_INPUT", minute: 0, ["content": request("List the files")]),
        line(1, "MODEL", "PLANNER_RESPONSE", minute: 0, ["tool_calls": [call("run_command", ["CommandLine": "ls", "Cwd": "/workspace/demo"])], "input_tokens": 100, "output_tokens": 10, "cache_read_tokens": 0]),
        line(2, "MODEL", "GENERIC", minute: 0, ["content": "Created At: x\nCompleted At: x\n\nThe command exited with code 0.\nOutput:\na.txt\n"]),
        line(3, "MODEL", "PLANNER_RESPONSE", minute: 0, ["content": "There is one file.", "input_tokens": 120, "output_tokens": 8, "cache_read_tokens": 50]),
        line(4, "USER_EXPLICIT", "USER_INPUT", minute: 1, ["content": request("Ask a helper to read a.txt")]),
        line(5, "MODEL", "PLANNER_RESPONSE", minute: 1, ["tool_calls": [call("invoke_subagent", ["Subagents": [["Role": "File Reader", "TypeName": "research", "Prompt": "Read a.txt", "Model": "inherit"]]])]]),
        line(6, "MODEL", "GENERIC", minute: 1, ["content": spawned(child)]),
        line(7, "MODEL", "PLANNER_RESPONSE", minute: 1, ["content": "Waiting for the helper."]),
        line(8, "SYSTEM", "SYSTEM_MESSAGE", minute: 3, ["content": message(from: child, "It says hi")]),
        line(9, "MODEL", "PLANNER_RESPONSE", minute: 3, ["content": "The file says hi."]),
        line(10, "USER_EXPLICIT", "USER_INPUT", minute: 4, ["content": request("Edit it")]),
        line(11, "MODEL", "PLANNER_RESPONSE", minute: 4, ["tool_calls": [call("write_to_file", ["TargetFile": "/workspace/demo/a.txt", "CodeContent": "new"])]]),
        line(12, "MODEL", "GENERIC", status: "ERROR", minute: 4, ["content": "Created At: x\nCompleted At: x\nEncountered error", "error": "permission check failed: user denied permission"]),
    ]

    private func temporary() throws -> URL {
        let path = realpath(FileManager.default.temporaryDirectory.path, nil).map { resolved in defer { free(resolved) }; return String(cString: resolved) }!
        let url = URL(fileURLWithPath: path).appendingPathComponent("mc-agy-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }
    @discardableResult private func writeTranscript(home: URL, _ conversation: String, _ lines: [String], trailing: String = "") throws -> URL {
        let url = try #require(AntigravityRecords.transcript(brain: AntigravityRecords.brain(home: home), conversation: conversation))
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data((lines.joined(separator: "\n") + "\n" + trailing).utf8).write(to: url)
        return url
    }
    private func objects(_ lines: [String]) -> [[String: Any]] { lines.compactMap { (try? JSONSerialization.jsonObject(with: Data($0.utf8))) as? [String: Any] } }
    private func latest(_ nodes: [ExecutionGraphNode]) -> [String: ExecutionGraphNode] { Dictionary(nodes.map { ($0.id, $0) }, uniquingKeysWith: { $1 }) }

    // MARK: The live graph

    @Test func aRunDrawsTheRequestItsSubagentsWorkAndTheLatestAnswer() throws {
        try LocaleOverride.$language.withValue(.en) { () throws in
            var nodes: [ExecutionGraphNode] = [], activities: [AgentActivity] = [], logs: [(String, String)] = []
            let parser = CLIStreamParser(provider: "gemini", log: { logs.append(($0, $1)) }, resume: { _ in }, activityNamespace: "agy-run",
                                         activity: { activities.append($0) }, graph: { nodes.append($0) }, graphInput: "Ask a helper to read a.txt", configuredModel: "default")
            parser.push(Self.subagentStream)
            let started = try #require(parser.antigravitySubagents.first)
            #expect(parser.antigravitySubagents == [AntigravitySubagent(conversation: Self.child, parent: Self.parent, title: "File Reader (research)", prompt: "Read a.txt")])
            // The sub-agent's own transcript, as the runner's watcher reads it.
            let record = AntigravityChildRecord(started, namespace: "agy-run")
            for object in objects(Self.childLines) { record.consume(object) }
            #expect(record.takeSpawned().map(\.conversation) == [Self.grandchild])
            parser.receiveAntigravitySessions([try #require(record.take())])
            parser.flush(); parser.finishActivities(stopped: false); parser.finishGraph(state: "completed")

            let graph = latest(nodes)
            let main = try #require(graph[ExecutionGraphSupport.mainNodeID(runId: "agy-run")])
            #expect(main.title == "Antigravity" && main.state == "completed" && main.input == "Ask a helper to read a.txt")
            // The latest reply is the answer, exactly as the transcript shows it.
            #expect(main.output == "The file says hi.")
            #expect(logs.filter { $0.0 == "assistant" }.map(\.1) == ["Waiting for the helper.", "The file says hi."])
            #expect(main.usage == GraphTokenUsage(inputTokens: 350, outputTokens: 21, cacheReadTokens: 40))
            #expect(main.responseRecords?.count == 3 && main.responseRecords?.allSatisfy { $0.model == "gemini-3.8-flash-low" && !$0.markedAsConfigured } == true)

            let agent = try #require(graph.values.first { $0.kind == "agent" })
            #expect(agent.parentId == main.id && agent.title == "File Reader (research)" && agent.input == "Read a.txt")
            #expect(agent.state == "completed" && agent.output == "It says hi")
            #expect(agent.entries.compactMap(\.activity?.toolName) == ["view_file", "invoke_subagent", "send_message"])
            #expect(agent.entries.compactMap(\.activity).allSatisfy { $0.state == "completed" && $0.provider == "gemini" })
            // A file it read is named, never copied.
            #expect(agent.entries.first { $0.activity?.toolName == "view_file" }?.activity.map { $0.summary == "/workspace/demo/a.txt" && $0.output == nil } == true)
            #expect(agent.entries.first { $0.activity?.toolName == "send_message" }?.activity?.summary == "Summary of send_message")
            #expect(agent.entries.last { $0.kind == "assistant" }?.text == "Sent the content back.")
            #expect(agent.usage == GraphTokenUsage(inputTokens: 110, outputTokens: 12, cacheReadTokens: 10))
            #expect(!agent.entries.contains { $0.text == L("graph.block.unfinished.agent") })
            // The start is one row in the request's own block.
            let row = try #require(activities.last { $0.toolName == "invoke_subagent" })
            #expect(row.kind == "agent" && row.state == "completed" && row.summary == "File Reader (research)")
        }
    }

    @Test func aPaneKeepsTheRunWithItsSubagentAndResult() throws {
        try LocaleOverride.$language.withValue(.en) { () throws in
            var session = RunSession(id: "pane", workspaceId: "space", title: "Antigravity", provider: "gemini")
            session.recordGraph(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "request-one", kind: "user", text: "Ask a helper to read a.txt", provider: "gemini")))
            #expect(session.graphRuns?.count == 1)
            let parser = CLIStreamParser(provider: "gemini", log: { session.recordGraph(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(kind: $0, text: $1, provider: "gemini"))) },
                                         resume: { _ in }, activityNamespace: "agy-run", activity: { session.recordGraph(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: $0.id, kind: "system", text: $0.summary, provider: "gemini", activity: $0))) },
                                         graph: { session.recordGraph(RunEvent(sessionId: "pane", type: "graph", graph: $0)) }, graphInput: "Ask a helper to read a.txt", configuredModel: "default")
            parser.push(Self.subagentStream)
            let record = AntigravityChildRecord(try #require(parser.antigravitySubagents.first), namespace: "agy-run")
            for object in objects(Self.childLines) { record.consume(object) }
            parser.receiveAntigravitySessions([try #require(record.take())])
            parser.flush(); parser.finishActivities(stopped: false); parser.finishGraph(state: "completed")
            session.recordGraph(RunEvent(sessionId: "pane", type: "status", status: "completed"))
            let run = try #require(session.mightyGraphRuns.last)
            #expect(run.provider == "gemini" && run.status == "completed" && run.finalOutput == "The file says hi.")
            #expect(run.resultEntries.map(\.text) == ["The file says hi."])
            // The answer is in the request's block once, not again as a copy of the result.
            #expect(run.rootEntries.filter { $0.kind == "assistant" && $0.text == "The file says hi." }.count == 1)
            #expect(run.rootEntries.contains { $0.activity?.toolName == "invoke_subagent" })
            let agent = try #require(run.agents.first)
            #expect(run.agents.count == 1 && agent.title == "File Reader (research)" && agent.status == "completed")
            #expect(agent.entries.filter { $0.kind == "assistant" }.map(\.text) == ["Sent the content back.", "It says hi"])
            session.agentViewMode = "mighty"
            #expect(MightyGraphSupport.providers.contains("gemini") && PlanCardSupport.showsDiagram(session))
        }
    }

    @Test func aNewInstructionReopensASettledSubagentAndKeepsItsAnswer() throws {
        try LocaleOverride.$language.withValue(.en) { () throws in
            var nodes: [ExecutionGraphNode] = []
            let parser = CLIStreamParser(provider: "gemini", log: { _, _ in }, resume: { _ in }, activityNamespace: "agy-run", graph: { nodes.append($0) })
            parser.push(Self.subagentStream)
            let record = AntigravityChildRecord(try #require(parser.antigravitySubagents.first), namespace: "agy-run")
            for object in objects(Self.childLines) { record.consume(object) }
            parser.receiveAntigravitySessions([try #require(record.take())])
            for object in objects([Self.line(9, "SYSTEM", "SYSTEM_MESSAGE", minute: 5, ["content": Self.message(from: Self.parent, "Now read b.txt")]),
                                   Self.line(10, "MODEL", "PLANNER_RESPONSE", minute: 5, ["tool_calls": [Self.call("send_message", ["Recipient": Self.parent, "Message": "b says bye"])]]),
                                   Self.line(11, "MODEL", "GENERIC", minute: 5, ["content": "Message sent."]),
                                   Self.line(12, "MODEL", "PLANNER_RESPONSE", minute: 5, ["content": "Done again."])]) { record.consume(object) }
            let next = try #require(record.take())
            #expect(next.turns == 2 && next.entries.first?.kind == "user" && next.entries.first?.text == "Now read b.txt")
            parser.receiveAntigravitySessions([next])
            let agent = try #require(latest(nodes).values.first { $0.kind == "agent" })
            #expect(agent.activityGeneration == 1 && agent.state == "completed" && agent.output == "b says bye")
            #expect(agent.entries.filter { $0.kind == "assistant" }.map(\.text) == ["Sent the content back.", "It says hi", "Done again."])
        }
    }

    // MARK: Following sub-agents

    @Test func theWatcherFollowsGrowingTranscriptsAndNestedSubagents() throws {
        let home = try temporary(); defer { try? FileManager.default.removeItem(at: home) }
        let brain = AntigravityRecords.brain(home: home)
        let watcher = AntigravitySubagentWatcher(brain: brain, namespace: "agy-run")
        let started = [AntigravitySubagent(conversation: Self.child, parent: Self.parent, title: "File Reader (research)", prompt: "Read a.txt")]
        // Not written yet: nothing to read, and no error.
        #expect(watcher.poll(started).isEmpty)
        // A line still being written is not read.
        let half = String(Self.childLines[2].prefix(20))
        let url = try writeTranscript(home: home, Self.child, Array(Self.childLines[0...1]), trailing: half)
        let first = try #require(watcher.poll(started).first)
        #expect(first.thread == Self.child && first.state == "running" && first.turns == 1 && first.input == "Read a.txt")
        #expect(first.entries.compactMap(\.activity).map(\.state) == ["running"])
        // The rest arrives; the helper it starts is read in the same pass.
        let handle = try FileHandle(forWritingTo: url); try handle.seekToEnd()
        try handle.write(contentsOf: Data((String(Self.childLines[2].dropFirst(20)) + "\n" + Self.childLines[3...].joined(separator: "\n") + "\n").utf8)); try handle.close()
        try writeTranscript(home: home, Self.grandchild, Self.grandchildLines)
        let second = watcher.poll(started)
        #expect(second.map(\.thread) == [Self.child, Self.grandchild])
        #expect(second[0].state == "completed" && second[0].output == "It says hi" && second[0].entries.contains { $0.id == first.entries.first?.id && $0.activity?.state == "completed" })
        #expect(second[1].parentThread == Self.child && second[1].title == "Checker (research)" && second[1].input == "Check it" && second[1].output == "Checked.")
        #expect(watcher.poll(started).isEmpty)
        #expect(watcher.finish(started).isEmpty && watcher.stopped && watcher.poll(started).isEmpty)
        // The run's own conversation is never followed as a sub-agent.
        let rooted = AntigravitySubagentWatcher(brain: brain, namespace: "agy-run", root: Self.child)
        #expect(rooted.poll(started).isEmpty)
        // Only conversation ids name a transcript.
        #expect(AntigravityRecords.transcript(brain: brain, conversation: "../escape") == nil)
    }

    // MARK: Older requests

    @Test func olderRequestsAreReadFromTheConversationTranscript() throws {
        try LocaleOverride.$language.withValue(.en) { () throws in
            let home = try temporary(); defer { try? FileManager.default.removeItem(at: home) }
            try writeTranscript(home: home, Self.parent, Self.parentLines)
            try writeTranscript(home: home, Self.child, Self.childLines)
            try writeTranscript(home: home, Self.grandchild, Self.grandchildLines)
            #expect(SessionHistory.providers.contains("gemini"))
            #expect(SessionHistory.locate(provider: "gemini", resumeID: Self.parent, workspacePath: "/workspace/demo", environment: [:], home: home)?.lastPathComponent == "transcript_full.jsonl")
            #expect(SessionHistory.locate(provider: "gemini", resumeID: Self.grandchild.replacingOccurrences(of: "3", with: "9"), workspacePath: "/x", environment: [:], home: home) == nil)

            let chunk = try SessionHistory.load(SessionHistoryRequest(provider: "gemini", resumeID: Self.parent, workspacePath: "/workspace/demo", home: home))
            #expect(chunk.reachedStart && chunk.runs.map(\.input) == ["List the files", "Ask a helper to read a.txt", "Edit it"])
            #expect(chunk.runs.allSatisfy { $0.provider == "gemini" && $0.id.hasPrefix("record-") })
            #expect(chunk.runs.map(\.status) == ["completed", "completed", "stopped"])

            let command = chunk.runs[0]
            #expect(command.finalOutput == "There is one file." && command.resultEntries.map(\.text) == ["There is one file."])
            let row = try #require(command.rootEntries.compactMap(\.activity).first { $0.toolName == "run_command" })
            #expect(row.state == "completed" && row.summary == "ls" && row.output?.contains("a.txt") == true && row.output?.contains("Created At") == false)
            #expect(command.usage == GraphTokenUsage(inputTokens: 220, outputTokens: 18, cacheReadTokens: 50))
            #expect(command.rootEntries.first?.timestamp == "2026-01-02T03:00:00Z")

            let delegated = chunk.runs[1]
            #expect(delegated.finalOutput == "The file says hi.")
            #expect(delegated.agents.map(\.title) == ["File Reader (research)", "Checker (research)"])
            let reader = delegated.agents[0], checker = delegated.agents[1]
            #expect(reader.parentID == nil && reader.input == "Read a.txt" && reader.status == "completed")
            #expect(reader.entries.compactMap(\.activity?.toolName) == ["view_file", "invoke_subagent", "send_message"])
            #expect(!reader.entries.contains { $0.text.contains("secret file line") || $0.activity?.output?.contains("secret file line") == true })
            #expect(checker.parentID == reader.id && checker.input == "Check it" && checker.status == "completed")

            let declined = chunk.runs[2]
            #expect(declined.agents.isEmpty && declined.resultEntries.isEmpty)
            let edit = try #require(declined.rootEntries.compactMap(\.activity).first { $0.toolName == "write_to_file" })
            #expect(edit.state == "error" && edit.output?.contains("user denied permission") == true)

            // Ten at a time from the end, as for Claude and Codex.
            let newest = try SessionHistory.load(SessionHistoryRequest(provider: "gemini", resumeID: Self.parent, workspacePath: "/x", home: home, turns: 1))
            #expect(newest.runs.map(\.input) == ["Edit it"] && !newest.reachedStart)
            // Above a retained request: what is on screen is passed over.
            let above = try SessionHistory.load(SessionHistoryRequest(provider: "gemini", resumeID: Self.parent, workspacePath: "/x", home: home,
                                                                      anchor: SessionHistoryAnchor(text: "Edit it", date: AgentRunTiming.parseTimestamp("2026-01-02T03:04:01Z"))))
            #expect(above.runs.map(\.input) == ["List the files", "Ask a helper to read a.txt"])
        }
    }

    @Test func recordedStepsBecomeTheEventsAgyPrinted() throws {
        let steps = objects(Self.parentLines)
        let answered = AntigravityRecords.streamEvents(Array(steps[1...3]), conversation: Self.parent)
        #expect(answered.status == "completed")
        #expect(answered.events.map { ($0["event"] as? String ?? "") + ":" + ((($0["step_update"] as? [String: Any])?["step_type"] as? String) ?? "") }
                == ["init:", "step_update:agent_response", "step_update:tool", "step_update:agent_response", "result:"])
        // A call the model moved past without a result step stays open (it ends stopped).
        let open = AntigravityRecords.streamEvents([steps[11]], conversation: Self.parent)
        #expect(open.status == "stopped" && (open.events.last?["step_update"] as? [String: Any])?["state"] as? String == "ACTIVE")
        let failed = AntigravityRecords.streamEvents(objects([Self.line(1, "MODEL", "PLANNER_RESPONSE", status: "ERROR", ["error": "Quota exceeded"])]), conversation: Self.parent)
        #expect(failed.status == "error" && ((failed.events.last?["result"] as? [String: Any])?["error"] as? [String: Any])?["message"] as? String == "Quota exceeded")
        #expect(AntigravityRecords.request("<USER_REQUEST>\n  hello  \n</USER_REQUEST>\n<ADDITIONAL_METADATA>x</ADDITIONAL_METADATA>") == "hello")
        #expect(AntigravityRecords.message(Self.message(from: Self.parent, "Do it\nnow"), from: Self.parent) == "Do it\nnow")
        #expect(AntigravityRecords.message(Self.message(from: "system", "[Notice]"), from: Self.parent) == nil)
        #expect(AntigravityRecords.title(role: "", typeName: "research") == "research" && AntigravityRecords.title(role: nil, typeName: nil) == nil)
    }

    @Test func aLaterRequestsWorkForTheSameSubagentStaysWithThatRequest() throws {
        try LocaleOverride.$language.withValue(.en) { () throws in
            let home = try temporary(); defer { try? FileManager.default.removeItem(at: home) }
            let again = [Self.line(13, "USER_EXPLICIT", "USER_INPUT", minute: 6, ["content": Self.request("Ask the helper again")]),
                         Self.line(14, "MODEL", "PLANNER_RESPONSE", minute: 6, ["tool_calls": [Self.call("send_message", ["Recipient": Self.child, "Message": "Now read b.txt"])]]),
                         Self.line(15, "MODEL", "GENERIC", minute: 6, ["content": "Message sent."]),
                         Self.line(16, "MODEL", "PLANNER_RESPONSE", minute: 7, ["content": "Asked."])]
            let later = [Self.line(9, "SYSTEM", "SYSTEM_MESSAGE", minute: 6, ["content": Self.message(from: Self.parent, "Now read b.txt")]),
                         Self.line(10, "MODEL", "PLANNER_RESPONSE", minute: 6, ["content": "b says bye"])]
            try writeTranscript(home: home, Self.parent, Self.parentLines + again)
            try writeTranscript(home: home, Self.child, Self.childLines + later)
            try writeTranscript(home: home, Self.grandchild, Self.grandchildLines)
            func reader(_ chunk: SessionHistoryChunk) throws -> MightyGraphAgent {
                try #require(chunk.runs.first { $0.input == "Ask a helper to read a.txt" }?.agents.first)
            }
            // Read at once, and as a later chunk above the newer requests.
            let whole = try SessionHistory.load(SessionHistoryRequest(provider: "gemini", resumeID: Self.parent, workspacePath: "/x", home: home))
            let newest = try SessionHistory.load(SessionHistoryRequest(provider: "gemini", resumeID: Self.parent, workspacePath: "/x", home: home, turns: 2))
            #expect(newest.runs.map(\.input) == ["Edit it", "Ask the helper again"])
            let older = try SessionHistory.load(SessionHistoryRequest(provider: "gemini", resumeID: Self.parent, workspacePath: "/x", home: home, file: newest.file, end: newest.end))
            for chunk in [whole, older] {
                let agent = try reader(chunk)
                #expect(agent.status == "completed" && agent.entries.filter { $0.kind == "assistant" }.map(\.text) == ["Sent the content back.", "It says hi"])
                #expect(!agent.entries.contains { $0.text.contains("b.txt") || $0.text == "b says bye" })
            }
        }
    }

    @Test func aSubagentWaitingForItsHelperIsNotDoneUntilItAnswers() throws {
        try LocaleOverride.$language.withValue(.en) { () throws in
            let child = AntigravitySubagent(conversation: Self.child, parent: Self.parent, title: "File Reader (research)", prompt: "Read a.txt")
            let record = AntigravityChildRecord(child, namespace: "agy-run")
            for object in objects([Self.childLines[0],
                                   Self.line(1, "MODEL", "PLANNER_RESPONSE", minute: 1, ["tool_calls": [Self.call("invoke_subagent", ["Subagents": [["Role": "Checker", "TypeName": "research", "Prompt": "Check it"]]])]]),
                                   Self.line(2, "MODEL", "GENERIC", minute: 1, ["content": Self.spawned(Self.grandchild)]),
                                   Self.line(3, "MODEL", "PLANNER_RESPONSE", minute: 1, ["content": "Waiting for my helper."])]) { record.consume(object) }
            let waiting = try #require(record.take())
            #expect(waiting.state == "running" && waiting.output == nil)
            for object in objects([Self.line(4, "SYSTEM", "SYSTEM_MESSAGE", minute: 2, ["content": Self.message(from: Self.grandchild, "Checked.")]),
                                   Self.line(5, "MODEL", "PLANNER_RESPONSE", minute: 2, ["content": "All checked."])]) { record.consume(object) }
            let done = try #require(record.take())
            #expect(done.state == "completed" && done.output == "All checked." && done.turns == 1)

            // A helper that reports again after it answered: the same assignment goes on, in the graph too.
            var session = RunSession(id: "pane", workspaceId: "space", title: "Antigravity", provider: "gemini")
            session.recordGraph(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "request-one", kind: "user", text: "Ask a helper", provider: "gemini")))
            var nodes: [ExecutionGraphNode] = []
            let parser = CLIStreamParser(provider: "gemini", log: { _, _ in }, resume: { _ in }, activityNamespace: "agy-run",
                                         graph: { nodes.append($0); session.recordGraph(RunEvent(sessionId: "pane", type: "graph", graph: $0)) })
            parser.push(Self.subagentStream)
            parser.receiveAntigravitySessions([waiting, done])
            #expect(session.mightyGraphRuns.last?.agents.first?.status == "completed")
            for object in objects([Self.line(6, "SYSTEM", "SYSTEM_MESSAGE", minute: 3, ["content": Self.message(from: Self.grandchild, "One more thing.")])]) { record.consume(object) }
            let continuing = try #require(record.take())
            #expect(continuing.state == "running" && continuing.turns == 1)
            parser.receiveAntigravitySessions([continuing])
            let agent = try #require(latest(nodes).values.first { $0.kind == "agent" })
            #expect(agent.state == "running" && (agent.activityGeneration ?? 0) == 0)
            #expect(session.mightyGraphRuns.last?.agents.first?.status == "running")
            for object in objects([Self.line(7, "MODEL", "PLANNER_RESPONSE", minute: 3, ["content": "Updated."])]) { record.consume(object) }
            parser.receiveAntigravitySessions([try #require(record.take())])
            #expect(latest(nodes).values.first { $0.kind == "agent" }.map { $0.state == "completed" && $0.output == "Updated." } == true)
        }
    }

    @Test func cancelledStepsStopAndRepliesKeepTheirStart() throws {
        try LocaleOverride.$language.withValue(.en) { () throws in
            let cancelled = AntigravityRecords.streamEvents(objects([Self.line(1, "MODEL", "PLANNER_RESPONSE", status: "CANCELED", ["content": "Half"])]), conversation: Self.parent)
            #expect(cancelled.status == "stopped" && !cancelled.events.contains { $0["event"] as? String == "result" })
            let tool = AntigravityRecords.streamEvents(objects([Self.parentLines[1], Self.line(2, "MODEL", "GENERIC", status: "CANCELED", ["content": "x"])]), conversation: Self.parent)
            #expect(((tool.events.last?["step_update"] as? [String: Any])?["state"] as? String) == "CANCELED" && tool.status == "stopped")
            var activities: [AgentActivity] = []
            let parser = CLIStreamParser(provider: "gemini", log: { _, _ in }, resume: { _ in }, activityNamespace: "agy-run", activity: { activities.append($0) })
            for event in tool.events { parser.receive(object: event) }
            #expect(activities.last { $0.toolName == "run_command" }?.state == "stopped")
            let record = AntigravityChildRecord(AntigravitySubagent(conversation: Self.child, parent: Self.parent), namespace: "agy-run")
            for object in objects([Self.childLines[0], Self.line(1, "MODEL", "PLANNER_RESPONSE", status: "INTERRUPTED", ["content": "Stopping"])]) { record.consume(object) }
            #expect(record.take()?.state == "stopped")

            var logs: [String] = []
            let reply = CLIStreamParser(provider: "gemini", log: { logs.append($1) }, resume: { _ in })
            reply.push(Self.step(["step_index": 1, "state": "DONE", "step_type": "agent_response", "text_delta": "    let x = 1\n\n"]) + "\n")
            #expect(logs == ["    let x = 1"])
            // However agy spaces its JSON, a request still opens.
            let spaced = Data(#"{"step_index": 0, "type": "USER_INPUT", "source": "USER_EXPLICIT", "content": "<USER_REQUEST>\nHi\n</USER_REQUEST>"}"#.utf8)
            #expect(AntigravityRecords.opening(spaced)?.prompt == "Hi")
        }
    }

    // MARK: Conversations to continue

    private func database(_ home: URL, rows: [(id: String, title: String, uris: [String], parent: String)]) throws {
        let url = AntigravityRecords.database(home: home)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        var db: OpaquePointer?
        guard sqlite3_open(url.path, &db) == SQLITE_OK, let db else { throw MightyError("database") }
        defer { sqlite3_close(db) }
        func exec(_ sql: String) throws { guard sqlite3_exec(db, sql, nil, nil, nil) == SQLITE_OK else { throw MightyError(String(cString: sqlite3_errmsg(db))) } }
        try exec("CREATE TABLE `conversation_summaries` (`conversation_id` text,`title` text NOT NULL DEFAULT \"\",`step_count` integer NOT NULL DEFAULT 0,`workspace_uris` text NOT NULL,`agent_name` text NOT NULL DEFAULT \"\",`parent_conversation_id` text NOT NULL DEFAULT \"\",PRIMARY KEY (`conversation_id`))")
        for row in rows {
            let uris = String(decoding: try JSONSerialization.data(withJSONObject: row.uris), as: UTF8.self).replacingOccurrences(of: "'", with: "''")
            try exec("INSERT INTO conversation_summaries (conversation_id, title, workspace_uris, parent_conversation_id) VALUES ('\(row.id)', '\(row.title)', '\(uris)', '\(row.parent)')")
        }
    }

    @Test func theConversationListShowsThisFoldersConversationsWithoutSubagents() throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let home = root.appendingPathComponent("home", isDirectory: true)
        let workspace = root.appendingPathComponent("work/demo project", isDirectory: true)
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        let other = "aaaaaaaa-0000-4000-8000-000000000004", automated = "aaaaaaaa-0000-4000-8000-000000000005", missing = "aaaaaaaa-0000-4000-8000-000000000006"
        let here = URL(fileURLWithPath: workspace.path, isDirectory: true).absoluteString
        try database(home, rows: [(Self.parent, "Summary title", ["file:///tmp/attachments", here], ""), (Self.child, "", [here], Self.parent),
                                  (other, "Elsewhere", ["file:///somewhere/else"], ""), (automated, "", [here], ""), (missing, "No transcript", [here], "")])
        try writeTranscript(home: home, Self.parent, Self.parentLines)
        try writeTranscript(home: home, Self.child, Self.childLines)
        try writeTranscript(home: home, other, Array(Self.parentLines[0...3]))
        try writeTranscript(home: home, automated, [Self.line(0, "USER_EXPLICIT", "USER_INPUT", ["content": Self.request("User: earlier text\nAssistant: more")])])

        let query = ResumableSessionQuery(workspacePath: workspace.path, home: home)
        let listing = ResumableSessions.listing(query, provider: "gemini")
        #expect(listing.items.map(\.sessionID) == [Self.parent] && listing.hidden == 1)
        let item = try #require(listing.items.first)
        #expect(item.provider == "gemini" && item.title == "List the files" && item.requests == 3 && item.model == nil && !item.automated)
        #expect(ResumableSessions.listing(ResumableSessionQuery(workspacePath: workspace.path, home: home, includeAutomated: true), provider: "gemini").items.count == 2)
        #expect(ResumableSessions.listing(query).items.map(\.provider) == ["gemini"])
        #expect(ResumableSessions.listing(ResumableSessionQuery(workspacePath: workspace.path, home: home, excluding: [Self.parent.uppercased()]), provider: "gemini").items.isEmpty)
        // "창 추가" asks first, and continuing one resumes that conversation.
        #expect(AddAgentPane.offersResume("gemini") && AddAgentPane.step(provider: "gemini", sessions: listing.items, inUse: []) == .askResumeOrNew)
        var pane = RunSession(workspaceId: "space", title: "Antigravity", provider: "gemini")
        ResumableSessions.apply(item, to: &pane)
        #expect(pane.resumeId == Self.parent && pane.title == "List the files")
        // The newest rows come first when the list is cut.
        #expect(AntigravityRecords.summaries(database: AntigravityRecords.database(home: home), limit: 2)?.map(\.conversation) == [missing, automated])
        // No database (agy never ran): nothing to list.
        #expect(ResumableSessions.listing(ResumableSessionQuery(workspacePath: workspace.path, home: root), provider: "gemini") == ResumableSessionListing())
    }

    // MARK: Skills

    @Test func skillsComeFromAgyTheUserAndTheWorkspace() throws {
        try LocaleOverride.$language.withValue(.en) { () throws in
            let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
            let home = root.appendingPathComponent("home"), workspace = root.appendingPathComponent("repo")
            func write(_ text: String, _ url: URL) throws {
                try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true); try Data(text.utf8).write(to: url)
            }
            let agy = AntigravityRecords.folder(home: home)
            try write("---\nname: automation\ndescription: Schedule a task\n---\n", agy.appendingPathComponent("builtin/skills/automation/SKILL.md"))
            try write("---\nname: review\ndescription: Mine\n---\n", agy.appendingPathComponent("skills/review/SKILL.md"))
            try write("---\nname: review\ndescription: The project's\n---\n", workspace.appendingPathComponent(".agents/skills/review/SKILL.md"))
            let commands = SlashCommandCatalog.commands(provider: "gemini", workspacePath: workspace.path, home: home)
            #expect(commands.map(\.invocation) == ["automation", "review"])
            #expect(commands[0].source == "Antigravity skill" && commands[1].description == "The project's" && commands[1].origin == .project)
        }
    }

    // MARK: A real run through the runner

    @Test func theRunnerReadsASubagentsTranscriptIntoTheGraph() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let home = root.appendingPathComponent("home", isDirectory: true), workspace = root.appendingPathComponent("work", isDirectory: true)
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        let transcript = try #require(AntigravityRecords.transcript(brain: AntigravityRecords.brain(home: home), conversation: Self.child))
        let lines = Self.childLines.filter { !$0.contains("invoke_subagent") && !$0.contains("Created the following") }
        try FileManager.default.createDirectory(at: root.appendingPathComponent("fixture"), withIntermediateDirectories: true)
        let stream = root.appendingPathComponent("fixture/stream.jsonl"), child = root.appendingPathComponent("fixture/child.jsonl")
        try Data(Self.subagentStream.utf8).write(to: stream)
        try Data((lines.joined(separator: "\n") + "\n").utf8).write(to: child)
        // A stand-in agy: the sub-agent writes its transcript under HOME while the stream runs.
        let binary = root.appendingPathComponent("fixture/agy")
        try Data("""
        #!/bin/sh
        if [ "$1" = "--version" ]; then printf '1.3.5\\n'; exit 0; fi
        if [ "$1" = "models" ]; then printf 'gemini-3.8-flash-low\\tGemini 3.8 Flash (Low)\\n'; exit 0; fi
        /bin/cat > /dev/null
        /bin/mkdir -p '\(transcript.deletingLastPathComponent().path)'
        /bin/cp '\(child.path)' '\(transcript.path)'
        /bin/cat '\(stream.path)'
        """.utf8).write(to: binary)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
        let events = AntigravityEvents()
        let service = ProviderService(binaryOverrides: ["gemini": binary], environment: ["HOME": home.path, "PATH": "/usr/bin:/bin"])
        let runner = ProcessRunner(providerService: service, pluginDirectory: root, onEvent: { events.append($0) })
        let space = Workspace(id: "space", name: "Fixture", path: workspace.path)
        do {
            try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: space.id, input: "Ask a helper to read a.txt", provider: "gemini"), workspace: space)
            let deadline = Date().addingTimeInterval(30)
            while !events.values().contains(where: { $0.type == "status" && $0.status != "running" }) {
                guard Date() < deadline else { throw MightyError("Timed out waiting for the fake agy run") }
                try await Task.sleep(for: .milliseconds(20))
            }
        } catch { await runner.shutdown(); await service.shutdown(); throw error }
        await runner.shutdown(); await service.shutdown()
        let values = events.values()
        #expect(values.last { $0.type == "status" }?.status == "completed")
        let graph = latest(values.compactMap(\.graph))
        let agent = try #require(graph.values.first { $0.kind == "agent" })
        #expect(agent.title == "File Reader (research)" && agent.state == "completed" && agent.output == "It says hi")
        #expect(agent.entries.compactMap(\.activity?.toolName) == ["view_file", "send_message"])
        #expect(graph.values.first { $0.kind == "main" }?.output == "The file says hi.")
    }
}

private final class AntigravityEvents: @unchecked Sendable {
    private let lock = NSLock(); private var items: [RunEvent] = []
    func append(_ item: RunEvent) { lock.lock(); items.append(item); lock.unlock() }
    func values() -> [RunEvent] { lock.lock(); defer { lock.unlock() }; return items }
}
