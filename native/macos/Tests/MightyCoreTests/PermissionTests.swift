import Testing
import Foundation
import Darwin
@testable import MightyCore

private final class PermissionRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var events: [RunEvent] = []
    func append(_ event: RunEvent) { lock.lock(); events.append(event); lock.unlock() }
    func values() -> [RunEvent] { lock.lock(); defer { lock.unlock() }; return events }
}

@Suite(.serialized)
struct PermissionTests {
    private func json(_ value: [String: Any]) throws -> Data { try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys, .withoutEscapingSlashes]) }
    private func ask(_ id: String = "ask-1", tool: String = "Read", input: [String: Any] = ["file_path": "~/.claude/CLAUDE.md"], interaction: Bool = false) throws -> Data {
        try json(["type": "control_request", "request_id": id, "request": ["subtype": "can_use_tool", "tool_name": tool, "tool_use_id": "tool-" + id, "input": input, "decision_reason": "Read outside the workspace", "blocked_path": "~/.claude/CLAUDE.md", "requires_user_interaction": interaction]])
    }
    private func initialize(_ channel: ClaudePermissionChannel) throws {
        channel.receive(try json(["type": "control_response", "response": ["subtype": "success", "request_id": channel.initializationId, "response": [:]]]))
    }
    private func response(_ data: Data) throws -> [String: Any] {
        let envelope = try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
        return try #require((envelope["response"] as? [String: Any])?["response"] as? [String: Any])
    }
    private func wait(_ predicate: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(6)
        while !predicate() {
            guard Date() < deadline else { throw MightyError("Permission fixture timed out") }
            try await Task.sleep(for: .milliseconds(20))
        }
    }

    @Test func localOptInUsesTheSupportedChannelWithoutChangingPermissionModeOrLegacyWire() throws {
        let plugin = URL(fileURLWithPath: "/tmp/plugin")
        for mode in ["manual", "plan", "acceptEdits", "auto", "fullAccess"] {
            let request = StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "Use the normal permission rules", settings: RunSettings(permissionMode: mode))
            let legacy = try ProviderService.arguments(request, pluginDirectory: plugin)
            #expect(legacy.contains("none")); #expect(!legacy.contains("stdio"))
            let prepared = try ProviderInput.prepare(request, pluginDirectory: plugin, attachments: AttachmentPreparation([]), allowPermissionPrompts: true)
            #expect(prepared.arguments.contains("host")); #expect(prepared.arguments.contains("stdio"))
            let index = try #require(prepared.arguments.firstIndex(of: "--permission-mode"))
            #expect(prepared.arguments[index + 1] == (mode == "fullAccess" ? "bypassPermissions" : mode))
            #expect(!prepared.arguments.contains("--dangerously-skip-permissions"))
            let envelope = try #require(JSONSerialization.jsonObject(with: prepared.standardInput) as? [String: Any])
            #expect(envelope["type"] as? String == "user")
            #expect((envelope["message"] as? [String: Any])?["content"] as? String == request.input)
        }
        let attachment = try AttachmentSupport.make(name: "notes.txt", data: Data("notes".utf8))
        let request = StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: "", attachments: [attachment])
        let staging = try AttachmentPreparation(request.attachments); defer { staging.cleanup() }
        let prepared = try ProviderInput.prepare(request, pluginDirectory: plugin, attachments: staging, allowPermissionPrompts: true)
        #expect(prepared.arguments.filter { $0 == "--input-format" }.count == 1)
        #expect(prepared.arguments.contains("--add-dir"))
        let oldEvent = RunEvent(sessionId: "pane", type: "status", status: "running")
        #expect(!String(decoding: try JSONEncoder().encode(oldEvent), as: UTF8.self).contains("permission"))
        #expect(try JSONDecoder().decode(RunEvent.self, from: JSONEncoder().encode(oldEvent)).permission == nil)
        #expect(!RemoteValidation.event(RunEvent(sessionId: "pane", type: "permission", permission: ToolPermissionRequest(id: "ask", runId: "run", toolUseId: "call", toolName: "Read", inputJSON: "{}", summary: "Read")), sessionId: "pane"))
    }

    @Test func oneTimeApprovalPreservesOriginalArgumentsAndDeduplicatesReplay() throws {
        var writes: [Data] = []; var displays: [ToolPermissionRequest] = []; var states: [String] = []; var failures: [String] = []
        let prompt = Data("user prompt frame\n".utf8)
        let channel = ClaudePermissionChannel(runId: "run-1", prompt: prompt, write: { writes.append($0) }, emit: { displays.append($0) }, activity: { _, state in states.append(state) }, warning: { _ in }, fail: { failures.append($0) })
        channel.start(); #expect(writes.count == 1); #expect(writes.first != prompt)
        try initialize(channel); #expect(writes.last == prompt)
        let input: [String: Any] = ["query": "Claude documentation", "nested": ["keep": [1, true, "\u{202e}visible"], "null": NSNull()]]
        let request = try ask(tool: "WebSearch", input: input)
        channel.receive(request); channel.receive(request)
        #expect(displays.count == 1); #expect(displays[0].runId == "run-1"); #expect(displays[0].canAllow)
        #expect(displays[0].inputJSON.contains("\\u202e"))
        let displayed = try #require(JSONSerialization.jsonObject(with: Data(displays[0].inputJSON.utf8)) as? NSDictionary)
        #expect(displayed.isEqual(to: input))
        try channel.respond(requestId: "ask-1", allow: true)
        let result = try response(try #require(writes.last))
        #expect(result["behavior"] as? String == "allow")
        #expect((result["updatedInput"] as? NSDictionary)?.isEqual(to: input) == true)
        #expect(result["updatedPermissions"] == nil); #expect(result["toolUseID"] as? String == "tool-ask-1")
        #expect(throws: MightyError.self) { try channel.respond(requestId: "ask-1", allow: true) }
        channel.receive(request)
        #expect(displays.map(\.state) == ["pending", "allowed"]); #expect(states == ["waiting", "running"])
        #expect(writes.count == 3); #expect(failures.isEmpty)
        channel.cancelAll(); #expect(displays.count == 2)
    }

    @Test func denialCancellationAndUnsupportedInteractionNeverGrantPermission() throws {
        var writes: [Data] = []; var displays: [ToolPermissionRequest] = []
        let channel = ClaudePermissionChannel(runId: "run", prompt: Data(), write: { writes.append($0) }, emit: { displays.append($0) }, activity: { _, _ in }, warning: { _ in }, fail: { _ in })
        channel.receive(try ask()); try channel.respond(requestId: "ask-1", allow: false)
        let denied = try response(try #require(writes.last))
        #expect(denied["behavior"] as? String == "deny"); #expect(denied["updatedInput"] == nil); #expect(denied["updatedPermissions"] == nil)
        channel.receive(try ask("cancel"))
        channel.receive(try json(["type": "control_cancel_request", "request_id": "cancel"]))
        #expect(throws: MightyError.self) { try channel.respond(requestId: "cancel", allow: true) }
        channel.receive(try ask("question", tool: "AskUserQuestion", input: ["questions": [["question": "Choose a region"]]]))
        #expect(displays.last?.canAllow == false)
        #expect(throws: MightyError.self) { try channel.respond(requestId: "question", allow: true) }
        try channel.respond(requestId: "question", allow: false)
        channel.receive(try ask("special", interaction: true)); #expect(displays.last?.canAllow == false)
        channel.cancelAll()
        #expect(displays.last?.state == "cancelled")
        #expect(throws: MightyError.self) { try channel.respond(requestId: "special", allow: true) }
        #expect(writes.count == 2)
        #expect(try writes.allSatisfy { try response($0)["behavior"] as? String == "deny" })
    }

    @Test func requestsAreBoundedAndInitializationFailureDoesNotSendThePrompt() throws {
        var writes: [Data] = []; var displays: [ToolPermissionRequest] = []; var warnings: [String] = []; var failures: [String] = []
        let channel = ClaudePermissionChannel(runId: "run", prompt: Data("not sent".utf8), write: { writes.append($0) }, emit: { displays.append($0) }, activity: { _, _ in }, warning: { warnings.append($0) }, fail: { failures.append($0) })
        channel.receive(try ask("large", input: ["command": String(repeating: "x", count: ClaudePermissionChannel.maximumInputBytes)]))
        #expect(displays.isEmpty); #expect(try response(try #require(writes.last))["behavior"] as? String == "deny")
        for index in 0..<17 { channel.receive(try ask("queued-\(index)")) }
        #expect(displays.count == 16); #expect(writes.count == 2); #expect(warnings.count == 2)
        channel.initializationTimedOut()
        #expect(channel.failed); #expect(failures.count == 1)
        #expect(displays.filter { $0.state == "cancelled" }.count == 16)
        try initialize(channel); #expect(writes.count == 2)
        var rejectedWrites: [Data] = []
        let rejected = ClaudePermissionChannel(runId: "run", prompt: Data("never".utf8), write: { rejectedWrites.append($0) }, emit: { _ in }, activity: { _, _ in }, warning: { _ in }, fail: { _ in })
        rejected.receive(try json(["type": "control_response", "response": ["subtype": "error", "request_id": rejected.initializationId, "error": "not supported"]]))
        #expect(rejected.failed); #expect(rejectedWrites.isEmpty)

        var replayWrites: [Data] = []; var replayDisplays: [ToolPermissionRequest] = []
        let replay = ClaudePermissionChannel(runId: "replay", prompt: Data("prompt\n".utf8), write: { replayWrites.append($0) }, emit: { replayDisplays.append($0) }, activity: { _, _ in }, warning: { _ in }, fail: { _ in })
        let outstanding = try (0..<17).map { try JSONSerialization.jsonObject(with: ask("replayed-\($0)")) }
        replay.receive(try json(["type": "control_response", "response": ["subtype": "success", "request_id": replay.initializationId, "response": [:], "pending_permission_requests": outstanding]]))
        #expect(replayDisplays.count == 16)
        #expect(try response(try #require(replayWrites.first))["behavior"] as? String == "deny")
        #expect(replayWrites.last == Data("prompt\n".utf8)); replay.cancelAll()
    }

    /// Recorded from `claude --resume` after a process that left `sleep 600` running in the background.
    @Test func aLeftoverTaskReportDoesNotEndTheRequest() throws {
        var logs: [String] = []; var results = 0
        let parser = CLIStreamParser(provider: "claude", log: { logs.append($1) }, resume: { _ in }, activityNamespace: "run", activity: { _ in }, result: { results += 1 })
        parser.push(try json(["type": "system", "subtype": "task_notification", "task_id": "b9f44slha", "status": "stopped", "summary": "Background shell command didn't finish before the previous session ended"]) + Data([10]))
        let leftover: [String: Any] = ["type": "result", "subtype": "success", "is_error": false, "num_turns": 0, "result": "", "total_cost_usd": 0, "origin": ["kind": "task-notification"]]
        parser.push(try json(leftover) + Data([10]))
        // Claude's stdin must stay open: the real turn has not started and may still ask for approval.
        #expect(results == 0 && logs.isEmpty)
        #expect(ClaudeStream.isNotificationResult(leftover))
        parser.push(try json(["type": "assistant", "message": ["content": [["type": "text", "text": "pong"]]]]) + Data([10]))
        let real: [String: Any] = ["type": "result", "subtype": "success", "is_error": false, "num_turns": 1, "result": "pong"]
        parser.push(try json(real) + Data([10])); parser.flush()
        #expect(results == 1 && logs == ["pong"])
        #expect(!ClaudeStream.isNotificationResult(real) && !ClaudeStream.isNotificationResult(["type": "result", "origin": ["kind": "user"]]))
        #expect(!ClaudeStream.isNotificationResult(["type": "assistant", "origin": ["kind": "task-notification"]]))
    }

    @Test func controlsDoNotLeakIntoLogsAndDirectPermissionStateOutranksDelayedMods() throws {
        var controls: [Data] = []; var logs: [String] = []; var activities: [AgentActivity] = []; var results = 0
        let parser = CLIStreamParser(provider: "claude", log: { logs.append($1) }, resume: { _ in }, activityNamespace: "run", activity: { activities.append($0) }, control: { controls.append($0) }, result: { results += 1 })
        parser.push(try ask(input: ["file_path": "~/.claude/CLAUDE.md", "credential": "only consent UI"]) + Data([10]))
        #expect(controls.count == 1); #expect(logs.isEmpty)
        let permission = ToolPermissionRequest(id: "ask-1", runId: "run", toolUseId: "tool-ask-1", toolName: "Read", inputJSON: "{}", summary: "~/.claude/CLAUDE.md")
        parser.permissionActivity(permission, state: "waiting")
        parser.push(try json(["type": "assistant", "message": ["content": [["type": "tool_use", "id": "tool-ask-1", "name": "Read", "input": ["file_path": "~/.claude/CLAUDE.md"]]]]]) + Data([10]))
        #expect(activities.last?.state == "waiting")
        parser.permissionActivity(permission, state: "running")
        parser.receiveMod(ModMetadata(claudeSessionId: "session", event: "tool.waiting", tool: "Read", toolUseId: "tool-ask-1", sequence: 1))
        #expect(activities.last?.state == "running")
        parser.push(try json(["type": "system", "subtype": "permission_denied", "tool_name": "Bash", "tool_use_id": "explicit-deny", "message": "Denied by a configured rule"]) + Data([10]))
        #expect(activities.last?.state == "error"); #expect(controls.count == 1)
        parser.push(try json(["type": "result", "subtype": "success", "is_error": false]) + Data([10])); parser.flush()
        #expect(results == 1); #expect(logs.isEmpty)
        #expect(!String(decoding: try JSONEncoder().encode(activities), as: UTF8.self).contains("only consent UI"))
    }

    @Test func realFakeCLIReceivesOneTimeAllowDenyAndRejectsStoppedOrPreviousRuns() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-permissions-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let binary = root.appendingPathComponent("claude")
        let source = #"""
        #!/bin/sh
        if [ "$1" = "--version" ]; then printf '2.1.273\n'; exit 0; fi
        metadata=false
        for argument in "$@"; do if [ "$argument" = "--safe-mode" ]; then metadata=true; fi; done
        IFS= read -r initialize || exit 21
        request_id=$(printf '%s' "$initialize" | /usr/bin/sed -E 's/.*"request_id":"([^"]+)".*/\1/')
        printf '{"type":"control_response","response":{"subtype":"success","request_id":"%s","response":{"models":[]}}}\n' "$request_id"
        if [ "$metadata" = true ]; then /bin/cat >/dev/null; exit 0; fi
        printf '%s\n' "$@" > arguments.txt
        printf '%s\n' "$$" > child-pid.txt
        IFS= read -r prompt || exit 22
        printf '%s\n' "$prompt" > prompt.json
        request='{"type":"control_request","request_id":"same-request","request":{"subtype":"can_use_tool","tool_name":"WebSearch","tool_use_id":"same-tool","input":{"query":"official documentation","allowed_domains":["example.com"],"nested":{"unchanged":true}}}}'
        if [ -f question-request.json ]; then request=$(/bin/cat question-request.json); fi
        printf '%s\n%s\n' "$request" "$request"
        IFS= read -r response || exit 23
        printf '%s\n' "$response" > response.json
        printf '%s\n' '{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"same-tool","content":"fixture result"}]}}'
        printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"fixture finished"}'
        # A real one-shot host must close stdin after result, not before consent.
        while IFS= read -r extra; do printf '%s\n' "$extra" >> extra-responses.jsonl; done
        """#
        try Data(source.utf8).write(to: binary); try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
        let plugin = root.appendingPathComponent("plugin")
        try FileManager.default.createDirectory(at: plugin.appendingPathComponent(".claude-plugin"), withIntermediateDirectories: true)
        try Data("{}".utf8).write(to: plugin.appendingPathComponent(".claude-plugin/plugin.json"))
        let providers = ProviderService(binaryOverrides: ["claude": binary])
        let events = PermissionRecorder()
        let runner = ProcessRunner(providerService: providers, pluginDirectory: plugin) { events.append($0) }
        let workspace = Workspace(id: "workspace", name: "Fixture", path: root.path)
        var request = StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "fixture request", settings: RunSettings(permissionMode: "manual"))
        do {
            try await runner.start(request: request, workspace: workspace, allowPermissionPrompts: true)
            try await wait { events.values().contains { $0.permission?.state == "pending" } }
            let first = try #require(events.values().compactMap(\.permission).first)
            #expect(events.values().filter { $0.permission?.state == "pending" }.count == 1)
            await #expect(throws: MightyError.self) { try await runner.respondToPermission(sessionId: "pane", runId: "other-run", requestId: first.id, allow: true) }
            #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("response.json").path))
            try await runner.respondToPermission(sessionId: "pane", runId: first.runId, requestId: first.id, allow: true)
            await #expect(throws: MightyError.self) { try await runner.respondToPermission(sessionId: "pane", runId: first.runId, requestId: first.id, allow: true) }
            try await wait { events.values().contains { $0.status == "completed" } }
            let allowed = try response(Data(contentsOf: root.appendingPathComponent("response.json")))
            #expect(allowed["behavior"] as? String == "allow"); #expect(allowed["updatedPermissions"] == nil)
            #expect((allowed["updatedInput"] as? NSDictionary)?.isEqual(to: ["query": "official documentation", "allowed_domains": ["example.com"], "nested": ["unchanged": true]]) == true)
            #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("extra-responses.jsonl").path))
            let flags = try String(contentsOf: root.appendingPathComponent("arguments.txt"), encoding: .utf8)
            #expect(flags.contains("--permission-prompts\nhost\n")); #expect(flags.contains("--permission-prompt-tool\nstdio\n")); #expect(flags.contains("--permission-mode\nmanual\n"))

            // Auto can still ask through the same channel (explicit ask rules,
            // protected actions or classifier fallback); it never auto-replies.
            request.settings.permissionMode = "auto"
            try await runner.start(request: request, workspace: workspace, allowPermissionPrompts: true)
            try await wait { events.values().filter { $0.permission?.state == "pending" }.count == 2 }
            let second = try #require(events.values().compactMap(\.permission).last { $0.state == "pending" })
            #expect(second.id == first.id); #expect(second.runId != first.runId)
            await #expect(throws: MightyError.self) { try await runner.respondToPermission(sessionId: "pane", runId: first.runId, requestId: first.id, allow: true) }
            try await runner.respondToPermission(sessionId: "pane", runId: second.runId, requestId: second.id, allow: false)
            try await wait { events.values().filter { $0.status == "completed" }.count == 2 }
            #expect(try response(Data(contentsOf: root.appendingPathComponent("response.json")))["behavior"] as? String == "deny")
            let autoFlags = try String(contentsOf: root.appendingPathComponent("arguments.txt"), encoding: .utf8)
            #expect(autoFlags.contains("--permission-mode\nauto\n")); #expect(autoFlags.contains("--permission-prompts\nhost\n"))
            #expect(!autoFlags.contains("bypassPermissions")); #expect(!autoFlags.contains("--allowedTools"))

            try FileManager.default.removeItem(at: root.appendingPathComponent("response.json"))
            try await runner.start(request: request, workspace: workspace, allowPermissionPrompts: true)
            try await wait { events.values().filter { $0.permission?.state == "pending" }.count == 3 }
            let third = try #require(events.values().compactMap(\.permission).last { $0.state == "pending" })
            await runner.stop(id: "pane")
            #expect(events.values().contains { $0.permission?.runId == third.runId && $0.permission?.state == "cancelled" })
            #expect(events.values().last?.status == "stopped")
            await #expect(throws: MightyError.self) { try await runner.respondToPermission(sessionId: "pane", runId: third.runId, requestId: third.id, allow: true) }
            #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("response.json").path))
            let pid = try #require(Int32(String(contentsOf: root.appendingPathComponent("child-pid.txt"), encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)))
            #expect(Darwin.kill(pid, 0) != 0)

            let questionInput: [String: Any] = ["questions": [["header": "앱", "question": "어느 앱?", "multiSelect": false, "options": [["label": "Swift", "description": "Mac"], ["label": "WinUI", "description": "Windows"]]]]]
            try ask("same-request", tool: "AskUserQuestion", input: questionInput).write(to: root.appendingPathComponent("question-request.json"))
            try await runner.start(request: request, workspace: workspace, allowPermissionPrompts: true)
            try await wait { events.values().filter { $0.permission?.state == "pending" }.count == 4 }
            let fourth = try #require(events.values().compactMap(\.permission).last { $0.state == "pending" })
            let answers = ["어느 앱?": UserQuestionAnswer(selectedOptions: ["Swift"])]
            await #expect(throws: MightyError.self) { try await runner.answerUserQuestions(sessionId: "pane", runId: third.runId, requestId: fourth.id, answers: answers) }
            await #expect(throws: MightyError.self) { try await runner.answerUserQuestions(sessionId: "wrong-pane", runId: fourth.runId, requestId: fourth.id, answers: answers) }
            #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("response.json").path))
            try await runner.answerUserQuestions(sessionId: "pane", runId: fourth.runId, requestId: fourth.id, answers: answers)
            await #expect(throws: MightyError.self) { try await runner.answerUserQuestions(sessionId: "pane", runId: fourth.runId, requestId: fourth.id, answers: answers) }
            try await wait { events.values().filter { $0.status == "completed" }.count == 3 }
            let questionResponse = try response(Data(contentsOf: root.appendingPathComponent("response.json")))
            #expect((questionResponse["updatedInput"] as? [String: Any])?["answers"] as? [String: String] == ["어느 앱?": "Swift"])
            #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("extra-responses.jsonl").path))
        } catch { await runner.shutdown(); await providers.shutdown(); throw error }
        await runner.shutdown(); await providers.shutdown()
    }

    @Test func zeroExitBeforeInitializationOrResultIsAnErrorInsteadOfACompletedTurn() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-permission-exit-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let binary = root.appendingPathComponent("claude")
        let source = #"""
        #!/bin/sh
        if [ "$1" = "--version" ]; then printf '2.1.273\n'; exit 0; fi
        metadata=false
        for argument in "$@"; do if [ "$argument" = "--safe-mode" ]; then metadata=true; fi; done
        if [ "$metadata" = false ] && [ -f before-initialize ]; then exit 0; fi
        IFS= read -r initialize || exit 21
        request_id=$(printf '%s' "$initialize" | /usr/bin/sed -E 's/.*"request_id":"([^"]+)".*/\1/')
        printf '{"type":"control_response","response":{"subtype":"success","request_id":"%s","response":{"models":[]}}}\n' "$request_id"
        if [ "$metadata" = true ]; then /bin/cat >/dev/null; fi
        exit 0
        """#
        try Data(source.utf8).write(to: binary); try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
        try FileManager.default.createDirectory(at: root.appendingPathComponent(".claude-plugin"), withIntermediateDirectories: true)
        try Data("{}".utf8).write(to: root.appendingPathComponent(".claude-plugin/plugin.json"))
        let providers = ProviderService(binaryOverrides: ["claude": binary])
        let events = PermissionRecorder()
        let runner = ProcessRunner(providerService: providers, pluginDirectory: root) { events.append($0) }
        let workspace = Workspace(id: "workspace", name: "Fixture", path: root.path)
        do {
            try Data().write(to: root.appendingPathComponent("before-initialize"))
            try await runner.start(request: StartRunRequest(sessionId: "before", workspaceId: "workspace", input: "not a real model request"), workspace: workspace, allowPermissionPrompts: true)
            try await wait { events.values().contains { $0.sessionId == "before" && $0.status == "error" } }
            try FileManager.default.removeItem(at: root.appendingPathComponent("before-initialize"))
            try await runner.start(request: StartRunRequest(sessionId: "after", workspaceId: "workspace", input: "not a real model request"), workspace: workspace, allowPermissionPrompts: true)
            try await wait { events.values().contains { $0.sessionId == "after" && $0.status == "error" } }
            #expect(!events.values().contains { $0.status == "completed" })
            #expect(events.values().filter { $0.entry?.kind == "error" }.count == 2)
        } catch { await runner.shutdown(); await providers.shutdown(); throw error }
        await runner.shutdown(); await providers.shutdown()
    }
}
