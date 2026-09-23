import Testing
import Foundation
@testable import MightyCore

private final class ActivityRecorder<T: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var items: [T] = []
    func append(_ item: T) { lock.lock(); items.append(item); lock.unlock() }
    func values() -> [T] { lock.lock(); defer { lock.unlock() }; return items }
}

@Suite(.serialized)
struct ActivityTests {
    private func lines(_ values: [[String: Any]]) throws -> Data {
        var result = Data()
        for value in values { result.append(try JSONSerialization.data(withJSONObject: value)); result.append(10) }
        return result
    }
    private func wait(_ predicate: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(30)
        while !predicate() {
            guard Date() < deadline else { throw MightyError("Timed out waiting for activity fixture") }
            try await Task.sleep(for: .milliseconds(20))
        }
    }
    private func temporary() throws -> URL {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-activity-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }
    private func fakeGemini(_ directory: URL, gated: Bool = false) throws -> URL {
        let binary = directory.appendingPathComponent("gemini")
        let source = """
        #!/bin/sh
        if [ "$1" = "--version" ]; then printf '0.20.0\\n'; exit 0; fi
        input="$(/bin/cat)"
        printf '%s\\n' '{"type":"init","session_id":"fixture-session"}'
        printf '%s\\n' '{"type":"tool_use","tool_id":"read-1","tool_name":"read_file","parameters":{"path":"README.md"}}'
        printf '%s\\n' '{"type":"tool_result","tool_id":"read-1","status":"success","output":"file contents"}'
        printf '%s\\n' '{"type":"result","status":"success"}'
        \(gated ? "if [ \"$input\" = stop ]; then sleep 30; else while [ ! -f exit-gate ]; do sleep 0.02; done; fi" : "")
        """
        try Data(source.utf8).write(to: binary)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
        return binary
    }

    @Test func legacyLogsAndSavedActivitiesPreserveCompatibilityAndBounds() throws {
        let legacy = Data(#"{"id":"old-log","kind":"system","text":"legacy","timestamp":"2026-09-16T00:00:00Z"}"#.utf8)
        let old = try JSONDecoder().decode(LogEntry.self, from: legacy)
        #expect(old.activity == nil)
        #expect(!String(decoding: try JSONEncoder().encode(old), as: UTF8.self).contains("activity"))
        var damaged = try #require(JSONSerialization.jsonObject(with: legacy) as? [String: Any])
        damaged["activity"] = ["id": false]
        #expect(try JSONDecoder().decode(LogEntry.self, from: JSONSerialization.data(withJSONObject: damaged)).text == "legacy")
        let activity = AgentActivity(id: "tool-1", provider: "codex", kind: "command", state: "running", toolName: "command_execution", summary: "printf 한글", output: String(repeating: "🦀", count: 5000))
        let bounded = try #require(ActivitySupport.normalized(activity))
        #expect(bounded.output!.utf8.count <= ActivitySupport.maximumOutputBytes)
        #expect(ActivitySupport.valid(bounded)); #expect(!ActivitySupport.valid(activity))
        let workspace = Workspace(id: "workspace", name: "Fixture", path: "/tmp")
        let state = AppSnapshot(workspaces: [workspace], sessions: [RunSession(id: "pane", workspaceId: workspace.id, title: "Test", logs: [LogEntry(id: bounded.id, kind: "system", text: bounded.summary, provider: "codex", activity: bounded)])])
        let restored = StateRepository.decodeSnapshot(try JSONEncoder().encode(state))
        #expect(restored.sessions.first?.logs.first?.activity?.state == "stopped")
        #expect(restored.sessions.first?.logs.first?.activity?.summary == "printf 한글")
        #expect(restored.sessions.first?.logs.first?.activity?.output == bounded.output)
        #expect(!RemoteValidation.event(RunEvent(sessionId: "pane", type: "activity", activity: activity), sessionId: "pane"))
        #expect(RemoteValidation.event(RunEvent(sessionId: "pane", type: "activity", activity: bounded), sessionId: "pane"))
    }

    @Test func claudeToolBlocksAndModsUseOneIdentityAndNeverResurrectCompletedCalls() throws {
        var activities: [AgentActivity] = []; var logs: [String] = []
        let parser = CLIStreamParser(provider: "claude", log: { logs.append($1) }, resume: { _ in }, activityNamespace: "run-one", activity: { activities.append($0) })
        parser.push(try lines([["type": "assistant", "message": ["content": [["type": "thinking", "thinking": "private reasoning"], ["type": "tool_use", "id": "call-1", "name": "Bash", "input": ["command": "printf hello", "secret": "not-forwarded"]]]]]]))
        parser.receiveMod(ModMetadata(claudeSessionId: "claude-1", event: "tool.call", tool: "Bash", toolUseId: "call-1", summary: "printf hello", sequence: 1))
        parser.receiveMod(ModMetadata(claudeSessionId: "claude-1", event: "tool.waiting", tool: "Bash", toolUseId: "call-1", summary: "printf hello", sequence: 2))
        parser.receiveMod(ModMetadata(claudeSessionId: "claude-1", event: "tool.complete", tool: "Bash", toolUseId: "call-1", summary: "printf hello", output: "hello", isError: false, sequence: 3))
        parser.push(try lines([["type": "user", "message": ["content": [["type": "tool_result", "tool_use_id": "call-1", "content": "hello"]]]]]))
        parser.receiveMod(ModMetadata(claudeSessionId: "claude-1", event: "tool.waiting", tool: "Bash", toolUseId: "call-1", sequence: 2))
        parser.push(try lines([["type": "assistant", "message": ["content": [["type": "tool_use", "id": "call-1", "name": "Bash", "input": ["command": "printf hello"]]]]]]))
        parser.flush()
        #expect(activities.map(\.state) == ["running", "waiting", "completed"])
        #expect(Set(activities.map(\.id)).count == 1)
        #expect(activities.allSatisfy { $0.kind == "command" && $0.summary == "printf hello" })
        #expect(logs.isEmpty)
        let wire = String(decoding: try JSONEncoder().encode(activities), as: UTF8.self)
        #expect(!wire.contains("private reasoning")); #expect(!wire.contains("not-forwarded"))
        parser.receiveMod(ModMetadata(claudeSessionId: "claude-1", event: "turn.complete", reason: "answer", agentId: "child-agent"))
        #expect(activities.count == 3)
    }

    @Test func codexEventsExposeCommandsFilesWebAndMCPWithoutReasoningOrPromptDumps() throws {
        var activities: [AgentActivity] = []; var logs: [String] = []
        let parser = CLIStreamParser(provider: "codex", log: { logs.append($1) }, resume: { _ in }, activityNamespace: "run-one", activity: { activities.append($0) })
        parser.push(try lines([
            ["type": "turn.started"],
            ["type": "item.started", "item": ["id": "cmd", "type": "command_execution", "command": "rg TODO Sources", "status": "in_progress", "aggregated_output": ""]],
            ["type": "item.completed", "item": ["id": "cmd", "type": "command_execution", "command": "rg TODO Sources", "status": "failed", "exit_code": 1, "aggregated_output": "no match"]],
            ["type": "item.completed", "item": ["id": "files", "type": "file_change", "status": "completed", "changes": [["path": "app.swift", "kind": "update"]]]],
            ["type": "item.completed", "item": ["id": "web", "type": "web_search", "query": "official API"]],
            ["type": "item.completed", "item": ["id": "mcp", "type": "mcp_tool_call", "server": "docs", "tool": "lookup", "arguments": ["query": "layout", "token": "not-forwarded"], "status": "completed", "result": ["content": [["type": "text", "text": "API result"], ["type": "image", "data": "not-forwarded-image"]]]]],
            ["type": "item.completed", "item": ["id": "reason", "type": "reasoning", "text": "private reasoning"]],
            ["type": "item.completed", "item": ["id": "answer", "type": "agent_message", "text": "**Done**"]],
            ["type": "turn.completed"]
        ])); parser.flush()
        #expect(activities.filter { $0.kind == "command" }.map(\.state) == ["running", "error"])
        #expect(activities.contains { $0.kind == "edit" && $0.summary == "update app.swift" })
        #expect(activities.contains { $0.kind == "web" && $0.summary == "official API" })
        #expect(activities.contains { $0.toolName == "docs.lookup" && $0.summary == "layout" && $0.output == "API result" })
        #expect(activities.filter { $0.kind == "turn" }.allSatisfy { $0.state == "running" })
        #expect(logs == ["**Done**"])
        let wire = String(decoding: try JSONEncoder().encode(activities), as: UTF8.self)
        #expect(!wire.contains("private reasoning")); #expect(!wire.contains("not-forwarded"))
    }

    @Test func geminiEventsPairToolIDsAndDoNotInferWaitingOrWholeTurnFailure() throws {
        var activities: [AgentActivity] = []; var logs: [String] = []
        let parser = CLIStreamParser(provider: "gemini", log: { logs.append($1) }, resume: { _ in }, activityNamespace: "run-one", activity: { activities.append($0) })
        parser.push(try lines([
            ["type": "tool_use", "tool_id": "read", "tool_name": "read_file", "parameters": ["absolute_path": "/tmp/a.swift"]],
            ["type": "tool_result", "tool_id": "read", "status": "error", "error": ["message": "File missing"]],
            ["type": "tool_use", "tool_id": "write", "tool_name": "write_file", "parameters": ["file_path": "/tmp/a.swift", "content": "full file not copied"]],
            ["type": "message", "role": "assistant", "delta": true, "content": "OK"],
            ["type": "result", "status": "success"]
        ])); parser.flush(); parser.finishActivities(stopped: true)
        #expect(activities.filter { $0.toolName == "read_file" }.map(\.state) == ["running", "error"])
        #expect(activities.filter { $0.toolName == "write_file" }.map(\.state) == ["running", "stopped"])
        #expect(!activities.contains { $0.state == "waiting" })
        #expect(!parser.failed); #expect(logs == ["OK"])
    }

    @Test func toolDurationPairsEveryProviderAndFreezesAtFirstCompletion() throws {
        for provider in ProviderOptions.ids {
            var now: TimeInterval = 10
            var activities: [AgentActivity] = []
            let parser = CLIStreamParser(provider: provider, log: { _, _ in }, resume: { _ in }, activityNamespace: "duration-\(provider)", activity: { activities.append($0) }, activityClock: { now })
            let start: [String: Any]
            let end: [String: Any]
            switch provider {
            case "claude":
                start = ["type": "assistant", "message": ["content": [["type": "tool_use", "id": "call", "name": "Bash", "input": ["command": "printf fixture"]]]]]
                end = ["type": "user", "message": ["content": [["type": "tool_result", "tool_use_id": "call", "content": "fixture"]]]]
            case "codex":
                start = ["type": "item.started", "item": ["id": "call", "type": "command_execution", "command": "printf fixture"]]
                end = ["type": "item.completed", "item": ["id": "call", "type": "command_execution", "command": "printf fixture"]]
            default:
                start = ["type": "tool_use", "tool_id": "call", "tool_name": "run_shell_command", "parameters": ["command": "printf fixture"]]
                end = ["type": "tool_result", "tool_id": "call", "status": "success", "output": "fixture"]
            }
            parser.push(try lines([start])); now = 10.125
            parser.push(try lines([start])) // A repeated start never resets the clock.
            if provider == "claude" {
                parser.receiveMod(ModMetadata(claudeSessionId: "fixture", event: "tool.waiting", tool: "Bash", toolUseId: "call", sequence: 1))
            }
            now = 11.25
            parser.push(try lines([end]))
            let completed = try #require(activities.last)
            #expect(completed.durationMs == 1_250)
            #expect(ActivitySupport.durationLabel(completed) == "1.2초")
            let count = activities.count
            now = 90
            parser.push(try lines([end, start]))
            #expect(activities.count == count)
            #expect(activities.last?.durationMs == 1_250)
            #expect(activities.filter { ["running", "waiting"].contains($0.state) }.allSatisfy { $0.durationMs == nil })
        }
    }

    @Test func toolDurationHandlesModsStopOrphanAndBoundedEvictionWithoutInventingTime() throws {
        var now: TimeInterval = 1
        var activities: [AgentActivity] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "durations", activity: { activities.append($0) }, activityClock: { now })
        parser.receiveMod(ModMetadata(claudeSessionId: "fixture", event: "tool.call", tool: "Read", toolUseId: "paired", sequence: 1))
        now = 1.125
        parser.receiveMod(ModMetadata(claudeSessionId: "fixture", event: "tool.complete", tool: "Read", toolUseId: "paired", sequence: 3))
        now = 3
        parser.receiveMod(ModMetadata(claudeSessionId: "fixture", event: "tool.waiting", tool: "Read", toolUseId: "paired", sequence: 2))
        #expect(activities.last?.durationMs == 125)
        parser.receiveMod(ModMetadata(claudeSessionId: "fixture", event: "tool.complete", tool: "Read", toolUseId: "orphan", sequence: 1))
        #expect(activities.last?.durationMs == nil)
        parser.receiveMod(ModMetadata(claudeSessionId: "fixture", event: "tool.call", tool: "Read", toolUseId: "pending", sequence: 1))
        now = 68
        parser.finishActivities(stopped: true)
        #expect(activities.last?.state == "stopped"); #expect(activities.last?.durationMs == 65_000)
        #expect(ActivitySupport.durationLabel(try #require(activities.last)) == "1분 5초")
        let count = activities.count
        now = 100; parser.finishActivities(stopped: true)
        #expect(activities.count == count)
        for index in 0..<513 {
            parser.receiveMod(ModMetadata(claudeSessionId: "fixture", event: "tool.call", tool: "Read", toolUseId: "bounded-\(index)"))
        }
        now = 101
        parser.receiveMod(ModMetadata(claudeSessionId: "fixture", event: "tool.complete", tool: "Read", toolUseId: "bounded-0"))
        #expect(activities.last?.durationMs == nil)
    }

    @Test func toolDurationPersistsAcrossWireAndRestoreWithResilientOptionalMetadata() throws {
        let completed = AgentActivity(id: "timed", provider: "gemini", kind: "read", state: "completed", summary: "README.md", durationMs: 250)
        let event = RunEvent(sessionId: "pane", type: "activity", activity: completed)
        let decoded = try JSONDecoder().decode(RunEvent.self, from: JSONEncoder().encode(event))
        #expect(decoded.activity?.durationMs == 250); #expect(RemoteValidation.event(decoded, sessionId: "pane"))
        let workspace = Workspace(id: "workspace", name: "Fixture", path: "/tmp")
        let snapshot = AppSnapshot(workspaces: [workspace], sessions: [RunSession(id: "pane", workspaceId: workspace.id, title: "Times", logs: [LogEntry(kind: "system", text: "README.md", activity: completed)])])
        #expect(StateRepository.decodeSnapshot(try JSONEncoder().encode(snapshot)).sessions.first?.logs.first?.activity?.durationMs == 250)
        var malformed = try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(completed)) as? [String: Any])
        let invalidValues: [Any] = ["invalid", -1, ActivitySupport.maximumDurationMs + 1]
        for invalid in invalidValues {
            malformed["durationMs"] = invalid
            let retained = try JSONDecoder().decode(AgentActivity.self, from: JSONSerialization.data(withJSONObject: malformed))
            #expect(retained.summary == "README.md"); #expect(retained.durationMs == nil)
        }
        malformed.removeValue(forKey: "durationMs")
        #expect(try JSONDecoder().decode(AgentActivity.self, from: JSONSerialization.data(withJSONObject: malformed)).durationMs == nil)
        var invalid = completed; invalid.durationMs = .infinity
        #expect(!ActivitySupport.valid(invalid)); #expect(ActivitySupport.durationLabel(invalid) == nil)
        invalid = completed; invalid.state = "running"
        #expect(!ActivitySupport.valid(invalid)); #expect(ActivitySupport.durationLabel(invalid) == nil)
        #expect(ActivitySupport.durationLabel(completed) == "250ms")
    }

    @Test func longMarkdownDeltasKeepCodeFenceAndTableInOneMessageAndRemainBounded() throws {
        var logs: [(String, String)] = []
        let parser = CLIStreamParser(provider: "gemini", log: { logs.append(($0, $1)) }, resume: { _ in })
        let markdown = "# Result\n\n```swift\n" + String(repeating: "let text = \"한글\"\n", count: 3000) + "```\n\n| A | B |\n| --- | --- |\n| 1 | 2 |\n"
        for slice in stride(from: 0, to: markdown.count, by: 301) {
            let start = markdown.index(markdown.startIndex, offsetBy: slice)
            let end = markdown.index(start, offsetBy: 301, limitedBy: markdown.endIndex) ?? markdown.endIndex
            parser.push(try lines([["type": "message", "role": "assistant", "delta": true, "content": String(markdown[start..<end])]]))
        }
        #expect(logs.isEmpty)
        parser.push(try lines([["type": "result", "status": "success"]])); parser.flush()
        #expect(logs.count == 1); #expect(logs.first?.0 == "assistant"); #expect(logs.first?.1 == markdown)
        let workspace = Workspace(id: "workspace", name: "Fixture", path: "/tmp")
        let snapshot = AppSnapshot(workspaces: [workspace], sessions: [RunSession(id: "pane", workspaceId: workspace.id, title: "Markdown", logs: [LogEntry(kind: "assistant", text: markdown)])])
        #expect(StateRepository.decodeSnapshot(try JSONEncoder().encode(snapshot)).sessions.first?.logs.first?.text == markdown)
        parser.push(try lines([["type": "message", "role": "assistant", "delta": true, "content": String(repeating: "🦀", count: 40_000)], ["type": "result", "status": "success"]]))
        #expect(logs.filter { $0.0 == "assistant" }.last!.1.utf8.count <= 131_072)
        #expect(logs.last?.0 == "system")
    }

    @Test func realChildExitAndStopAreTheOnlyTerminalTurnActivities() async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let binary = try fakeGemini(directory, gated: true)
        let service = ProviderService(binaryOverrides: ["gemini": binary])
        let events = ActivityRecorder<RunEvent>()
        let runner = ProcessRunner(providerService: service, pluginDirectory: directory, onEvent: { events.append($0) })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        do {
            try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "complete", provider: "gemini"), workspace: workspace)
            try await wait { events.values().contains { $0.activity?.summary == "Gemini 응답 마무리 중" } }
            #expect(!events.values().contains { $0.activity?.kind == "turn" && $0.activity?.state == "completed" })
            try Data().write(to: directory.appendingPathComponent("exit-gate"))
            try await wait { events.values().contains { $0.status == "completed" } }
            let firstTurn = try #require(events.values().first { $0.activity?.kind == "turn" && $0.activity?.state == "completed" }?.activity)
            #expect(events.values().filter { $0.activity?.kind == "turn" && $0.activity?.state == "completed" }.count == 1)
            try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "stop", provider: "gemini"), workspace: workspace)
            try await wait { events.values().filter { $0.activity?.summary == "Gemini 응답 마무리 중" }.count == 2 }
            await runner.stop(id: "pane")
            let last = try #require(events.values().last { $0.activity?.kind == "turn" }?.activity)
            #expect(last.state == "stopped"); #expect(last.id != firstTurn.id)
            let before = events.values(); try await Task.sleep(for: .milliseconds(40)); #expect(events.values() == before)
        } catch { await runner.shutdown(); await service.shutdown(); throw error }
        await runner.shutdown(); await service.shutdown()
    }

    @Test func modsTransportAcceptsBoundedActivityAndRejectsRawInputAndForgedMetadata() async throws {
        let recorder = ActivityRecorder<ModMetadata>()
        let bridge = try ModBridge(onEvent: { recorder.append($0) })
        let env = try await bridge.start()
        #expect(env["MIGHTY_CLAUDE_ACTIVITY"] == "1")
        let url = try #require(URL(string: env["MIGHTY_CLAUDE_BRIDGE_URL"]!))
        let base: [String: Any] = ["version": 1, "runId": env["MIGHTY_CLAUDE_RUN_ID"]!, "claudeSessionId": "claude-1", "event": "tool.complete", "tool": "Read", "toolUseId": "call-1", "summary": "README.md", "output": "done", "isError": false, "sequence": 3]
        func post(_ value: [String: Any], authenticated: Bool = true) async throws -> Int {
            var request = URLRequest(url: url); request.httpMethod = "POST"
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            if authenticated { request.setValue("Bearer " + env["MIGHTY_CLAUDE_BRIDGE_TOKEN"]!, forHTTPHeaderField: "Authorization") }
            request.httpBody = try JSONSerialization.data(withJSONObject: value)
            let (_, response) = try await URLSession.shared.data(for: request)
            return (response as! HTTPURLResponse).statusCode
        }
        do {
            #expect(try await post(base) == 200)
            #expect(try await post(base, authenticated: false) == 401)
            for (key, value) in [("input", ["secret": "raw"] as Any), ("summary", String(repeating: "x", count: 1001) as Any), ("output", String(repeating: "x", count: 8193) as Any), ("tool", "Read\nforged" as Any), ("sequence", true as Any), ("isError", "false" as Any)] {
                var invalid = base; invalid[key] = value; #expect(try await post(invalid) == 400)
            }
            #expect(recorder.values().count == 1)
            #expect(recorder.values().first?.toolUseId == "call-1")
        } catch { await bridge.stop(); throw error }
        await bridge.stop()
    }

    @Test func remoteActivityNegotiationKeepsLegacyCursorAndTerminalStatus() async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let binary = try fakeGemini(directory)
        let unavailable = URL(fileURLWithPath: "/usr/bin/false")
        let providers = ProviderService(binaryOverrides: ["gemini": binary, "claude": unavailable, "codex": unavailable])
        let repository = StateRepository(directory: directory.appendingPathComponent("state"), legacyStateURL: nil)
        let workspace = try await repository.approveWorkspace(Workspace(name: "Fixture", path: directory.path))
        try await repository.save(AppSnapshot(workspaces: [workspace]))
        let host = RemoteService(repository: repository, providers: providers, pluginDirectory: directory, dataDirectory: directory.appendingPathComponent("remote"), onEvent: { _ in }, allowLoopbackForTests: true)
        do {
            let state = try await host.startSharing(workspaceIds: [workspace.id], port: 0)
            let address = try #require(state.host.address); let token = try #require(state.host.token)
            let target = try await RemoteTransport.resolve(ParsedRemoteAddress.parse(address), peers: [], allowLoopback: true)
            let request = WireStart(request: StartRunRequest(sessionId: "client-pane", workspaceId: workspace.id, input: "fixture", provider: "gemini"))
            let accepted = try JSONDecoder().decode(WireJob.self, from: await RemoteTransport.request(target, token: token, method: "POST", path: "/v1/runs", body: JSONEncoder().encode(request)))
            let path = "/v1/runs/\(accepted.jobId)/events?cursor=0"
            var modern: WirePoll?
            for _ in 0..<1000 {
                modern = try JSONDecoder().decode(WirePoll.self, from: await RemoteTransport.request(target, token: token, method: "GET", path: path))
                if modern?.done == true { break }; try await Task.sleep(for: .milliseconds(30))
            }
            let complete = try #require(modern); #expect(complete.done)
            #expect(complete.events.contains { $0.event.type == "activity" && $0.event.activity?.kind == "read" && $0.event.activity?.summary == "README.md" })
            #expect(complete.events.contains { $0.event.activity?.kind == "read" && $0.event.activity?.state == "completed" && $0.event.activity?.durationMs != nil })
            var legacy = URLRequest(url: URL(string: address + path)!)
            legacy.setValue("Bearer " + token, forHTTPHeaderField: "Authorization"); legacy.setValue("1", forHTTPHeaderField: "x-mighty-remote-version")
            let (data, response) = try await URLSession.shared.data(for: legacy)
            #expect((response as? HTTPURLResponse)?.statusCode == 200)
            let old = try JSONDecoder().decode(WirePoll.self, from: data)
            #expect(old.events.map(\.cursor) == complete.events.map(\.cursor)); #expect(old.cursor == complete.cursor)
            #expect(!old.events.contains { $0.event.type == "activity" })
            #expect(old.events.last?.event.status == "completed")
            #expect(old.events.contains { $0.event.entry?.activity?.summary == "README.md" })
        } catch { await host.shutdown(); await providers.shutdown(); throw error }
        await host.shutdown(); await providers.shutdown()
    }
}
