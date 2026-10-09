import Darwin
import Foundation
import Testing
@testable import MightyCore

/// The FakeClaude fixture built beside this test bundle, copied into `folder`
/// as `claude` and signed ad hoc: some toolchains leave a plain executable
/// unsigned, and an unsigned arm64 binary is killed at launch.
private func fakeClaudeExecutable(in folder: URL) throws -> URL {
    var info = Dl_info()
    guard dladdr(#dsohandle, &info) != 0, let image = info.dli_fname else { throw MightyError("The test bundle's path is unknown.") }
    var directory = URL(fileURLWithPath: String(cString: image)).deletingLastPathComponent()
    var built: URL?
    for _ in 0 ..< 6 where built == nil {
        let candidate = directory.appendingPathComponent("FakeClaude")
        if FileManager.default.isExecutableFile(atPath: candidate.path) { built = candidate }
        directory = directory.deletingLastPathComponent()
    }
    guard let built else { throw MightyError("FakeClaude was not built beside the tests.") }
    let copy = folder.appendingPathComponent("claude")
    try FileManager.default.copyItem(at: built, to: copy)
    let sign = Process()
    sign.executableURL = URL(fileURLWithPath: "/usr/bin/codesign")
    sign.arguments = ["--force", "--sign", "-", copy.path]
    sign.standardOutput = FileHandle.nullDevice; sign.standardError = FileHandle.nullDevice
    try sign.run(); sign.waitUntilExit()
    guard sign.terminationStatus == 0 else { throw MightyError("FakeClaude could not be signed.") }
    return copy
}

private func jsonLines(_ url: URL) -> [[String: Any]] {
    let text = (try? String(contentsOf: url, encoding: .utf8)) ?? ""
    return text.split(separator: "\n").compactMap { (try? JSONSerialization.jsonObject(with: Data($0.utf8))) as? [String: Any] }
}

private func text(of result: Any?) -> String {
    ((result as? [String: Any])?["content"] as? [[String: Any]] ?? []).compactMap { $0["text"] as? String }.joined(separator: "\n")
}

/// The app side of the delegation server: records which pane each call
/// resolved to, through the same gates the app applies, and refuses it.
private final class RecordingDelegation: DelegationRequestHandler, @unchecked Sendable {
    struct Call: Equatable { var pane: String; var tool: String; var arguments: [String: String] }
    private let lock = NSLock()
    private var recorded: [Call] = []
    var calls: [Call] { lock.withLock { recorded } }

    func handle(_ request: DelegationRequest, binding: PaneMCPBinding) async -> DelegationResponse {
        if let gated = DelegationIOHandler.gate(binding, isSwitchOn: true) { return gated }
        lock.withLock { recorded.append(Call(pane: binding.agentPaneId, tool: request.tool, arguments: request.arguments)) }
        return .refusal(.notReported)
    }
}

/// No terminal calls are made here.
private struct NoTerminalCalls: AgentIORequestHandler {
    func handle(_ request: AgentIORequest, binding: PaneMCPBinding) async -> AgentIOResponse { .failure("not under test") }
}

private final class EventLog: @unchecked Sendable {
    private let lock = NSLock()
    private var events: [RunEvent] = []
    func append(_ event: RunEvent) { lock.withLock { events.append(event) } }
    var all: [RunEvent] { lock.withLock { events } }
}

/// The scripted fake Claude, launched through the real ProcessRunner with the
/// delegation switch on, behind the app's real socket server.
@Suite(.serialized) struct FakeClaudeDelegationTests {
    private struct Fixture {
        let root: URL
        let fake: URL
        let workspace: Workspace
        let config: URL
        let script: URL
        let log: URL
        let bindings = PaneMCPBindingRegistry()
        let delegation = RecordingDelegation()
        let events = EventLog()
        let server: AgentIOSocketServer
        let runner: ProcessRunner

        init(script steps: [[String: Any]], sessionId: String = UUID().uuidString.lowercased()) throws {
            root = try shortTemporaryDirectory()
            fake = try fakeClaudeExecutable(in: root)
            let folder = root.appendingPathComponent("workspace", isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            workspace = Workspace(id: "ws-1", name: "workspace", path: folder.path)
            config = root.appendingPathComponent("claude-config", isDirectory: true)
            script = root.appendingPathComponent("script.json")
            log = root.appendingPathComponent("fake-claude.jsonl")
            try JSONSerialization.data(withJSONObject: ["sessionId": sessionId, "turns": [steps]]).write(to: script)
            let plugin = root.appendingPathComponent("plugin", isDirectory: true)
            try FileManager.default.createDirectory(at: plugin.appendingPathComponent(".claude-plugin"), withIntermediateDirectories: true)
            try Data("{\"name\":\"mighty\"}".utf8).write(to: plugin.appendingPathComponent(".claude-plugin/plugin.json"))
            let socketPath = root.appendingPathComponent("io.sock").path
            server = AgentIOSocketServer(socketPath: socketPath, bindings: bindings, handler: NoTerminalCalls(), delegation: delegation)
            try server.start()
            // The app-written config names the fixture in the app binary's
            // headless server modes, so the real MCP servers answer.
            let location = PaneMCPServerLocation(socketPath: socketPath, executable: fake)
            let service = ProviderService(binaryOverrides: ["claude": fake], environment: [
                "PATH": "/usr/bin:/bin:/usr/sbin:/sbin", "HOME": root.path, "CLAUDE_CONFIG_DIR": config.path,
                "FAKE_CLAUDE_SCRIPT": script.path, "FAKE_CLAUDE_LOG": log.path,
            ])
            let events = events
            runner = ProcessRunner(providerService: service, pluginDirectory: plugin, paneMCPServer: location, paneMCPBindings: bindings,
                                   delegationEnabled: { true }, onEvent: { events.append($0) })
        }

        func start(_ input: String, pane: String = "parent-pane") async throws {
            try await runner.start(request: StartRunRequest(sessionId: pane, workspaceId: workspace.id, kind: SessionKind.claude, input: input, provider: "claude"),
                                   workspace: workspace, allowPermissionPrompts: true)
        }

        /// The run's last status once it ended.
        func ended(pane: String = "parent-pane") async throws -> String {
            let deadline = Date().addingTimeInterval(30)
            while Date() < deadline {
                if let status = events.all.last(where: { $0.sessionId == pane && $0.type == "status" && $0.status != "running" })?.status { return status }
                try await Task.sleep(nanoseconds: 20_000_000)
            }
            throw MightyError("The fake Claude run did not end.")
        }

        /// The permission card for `tool` that is still open.
        func waitingPermission(_ tool: String) async throws -> ToolPermissionRequest {
            let deadline = Date().addingTimeInterval(30)
            while Date() < deadline {
                let cards = events.all.compactMap(\.permission)
                if let card = cards.last(where: { $0.toolName == tool && $0.state == "pending" }),
                   !cards.contains(where: { $0.id == card.id && $0.state != "pending" }) { return card }
                try await Task.sleep(nanoseconds: 20_000_000)
            }
            throw MightyError("No permission request for \(tool).")
        }

        var seen: [[String: Any]] { jsonLines(log) }
        func seen(_ event: String) -> [[String: Any]] { seen.filter { $0["event"] as? String == event } }

        func tearDown() {
            server.stop()
            try? FileManager.default.removeItem(at: root)
        }
    }

    @Test func itSpeaksStreamJSONThroughTheRealRunnerWithNoModel() async throws {
        let session = UUID().uuidString.lowercased()
        let fixture = try Fixture(script: [["say": "Looking at the children."], ["call": "list_children", "arguments": [:] as [String: String]], ["say": "Done."]], sessionId: session)
        defer { fixture.tearDown() }
        try await fixture.start("How are the children doing?")
        #expect(try await fixture.ended() == "completed")

        // The host's initialize control request was answered before the prompt.
        #expect(fixture.seen("initialize").count == 1)
        let argv = try #require(fixture.seen("argv").first?["arguments"] as? [String])
        for flag in ["--print", "--verbose", "--output-format", "--input-format", "--permission-prompt-tool", "--mcp-config"] { #expect(argv.contains(flag)) }
        #expect(optionValue("--input-format", argv) == "stream-json" && optionValue("--output-format", argv) == "stream-json")

        // The app's stream-json parser read the fake's frames: the session id
        // from system/init, the assistant text, and a successful result.
        let events = fixture.events.all
        #expect(events.contains { $0.type == "resume" && $0.resumeId == session })
        let logged = events.compactMap(\.entry).map(\.text)
        #expect(logged.contains { $0.contains("Looking at the children.") })
        #expect(logged.contains { $0.contains("Done.") })
        #expect(events.filter { $0.type == "status" }.map(\.status) == ["running", "completed"])
        #expect(fixture.seen("exit").first?["code"] as? Int == 0)
    }

    @Test func itReachesMightyDelegationFromTheAppWrittenConfigWithThePaneToken() async throws {
        let fixture = try Fixture(script: [
            ["call": "list_children", "arguments": [:] as [String: String]],
            ["call": "child_status", "arguments": ["child": "child-1"]],
            ["call": "merge", "arguments": ["child": "child-1", "expected_head": String(repeating: "b", count: 40)]],
        ])
        defer { fixture.tearDown() }
        try await fixture.start("Merge child-1 if it reported.")
        #expect(try await fixture.ended() == "completed")

        // The fake started what the app-written config names for mighty-delegation.
        let argv = try #require(fixture.seen("argv").first?["arguments"] as? [String])
        let config = try #require(optionValue("--mcp-config", argv))
        let servers = try #require((try JSONSerialization.jsonObject(with: Data(config.utf8)) as? [String: Any])?["mcpServers"] as? [String: [String: Any]])
        #expect(servers[DelegationMCPServer.serverName]?["command"] as? String == fixture.fake.path)
        #expect(servers[DelegationMCPServer.serverName]?["args"] as? [String] == [DelegationMCPServer.headlessArgument])
        // The real server listed exactly the six tools to it.
        let connected = try #require(fixture.seen("mcp").first)
        #expect(connected["server"] as? String == DelegationMCPServer.serverName)
        #expect(connected["tools"] as? [String] == DelegationToolManifest.all.map(\.name))

        // Every call, in the script's order, resolved through the pane token to
        // this pane alone, and the app's refusal came back as the tool result.
        #expect(fixture.delegation.calls == [
            .init(pane: "parent-pane", tool: "list_children", arguments: [:]),
            .init(pane: "parent-pane", tool: "child_status", arguments: ["child": "child-1"]),
            .init(pane: "parent-pane", tool: "merge", arguments: ["child": "child-1", "expected_head": String(repeating: "b", count: 40)]),
        ])
        let calls = fixture.seen("call")
        #expect(calls.map { $0["tool"] as? String } == ["list_children", "child_status", "merge"])
        for call in calls {
            #expect(text(of: call["result"]).contains("Refused: not_reported. Nothing was changed."))
            #expect(((call["result"] as? [String: Any])?["structuredContent"] as? [String: Any])?["refused"] as? String == "not_reported")
        }

        // The token reached the fake through its environment only: it is in
        // no argument, no log line and no transcript.
        let token = try #require(fixture.bindings.binding(forPane: "parent-pane")?.token)
        #expect(!argv.joined(separator: " ").contains(token))
        let files = FileManager.default.enumerator(at: fixture.root, includingPropertiesForKeys: nil)?.compactMap { $0 as? URL } ?? []
        for file in files where ["jsonl", "json"].contains(file.pathExtension) {
            #expect(!((try? String(contentsOf: file, encoding: .utf8)) ?? "").contains(token))
        }
    }

    @Test func itRaisesPermissionRequestsInTheCLIFormat() async throws {
        let fixture = try Fixture(script: [
            ["permission": "Bash", "input": ["command": "git status"]],
            ["call": "child_status", "arguments": ["child": "child-1"], "ask": true],
            ["call": "list_children", "arguments": [:] as [String: String], "ask": true],
        ])
        defer { fixture.tearDown() }
        try await fixture.start("Check the tree, then the child.")

        // The app's permission channel parsed each can_use_tool request into a card.
        let bash = try await fixture.waitingPermission("Bash")
        #expect(bash.canAllow && bash.inputJSON.contains("git status"))
        try await fixture.runner.respondToPermission(sessionId: "parent-pane", runId: bash.runId, requestId: bash.id, allow: true)
        let status = try await fixture.waitingPermission("mcp__mighty-delegation__child_status")
        #expect(status.inputJSON.contains("child-1"))
        try await fixture.runner.respondToPermission(sessionId: "parent-pane", runId: status.runId, requestId: status.id, allow: false)
        let list = try await fixture.waitingPermission("mcp__mighty-delegation__list_children")
        try await fixture.runner.respondToPermission(sessionId: "parent-pane", runId: list.runId, requestId: list.id, allow: true)
        #expect(try await fixture.ended() == "completed")

        // The fake got the host's answers in the CLI's control_response form.
        let answers = fixture.seen("permission")
        #expect(answers.map { $0["tool"] as? String } == ["Bash", "mcp__mighty-delegation__child_status", "mcp__mighty-delegation__list_children"])
        #expect(answers.map { $0["behavior"] as? String } == ["allow", "deny", "allow"])
        #expect(answers.first?["updatedInput"] as? [String: String] == ["command": "git status"])
        #expect(answers.first?["toolUseID"] as? String == bash.toolUseId)
        // A denied call never reaches the app; an allowed one does.
        #expect(fixture.delegation.calls.map(\.tool) == ["list_children"])
    }

    @Test func itWritesClaudeFormatTranscriptsUnderTheTempConfigDir() async throws {
        let session = UUID().uuidString.lowercased()
        let fixture = try Fixture(script: [["call": "list_children", "arguments": [:] as [String: String]], ["say": "No children yet."]], sessionId: session)
        defer { fixture.tearDown() }
        try await fixture.start("Delegate the release notes to a child.")
        #expect(try await fixture.ended() == "completed")

        // Where Claude Code keeps it, under the temp CLAUDE_CONFIG_DIR and not under HOME.
        let environment = ["CLAUDE_CONFIG_DIR": fixture.config.path]
        let record = try #require(SessionHistory.locate(provider: "claude", resumeID: session, workspacePath: fixture.workspace.path, environment: environment, home: fixture.root))
        #expect(record.path == fixture.config.appendingPathComponent("projects/\(SessionHistory.claudeProjectFolder(fixture.workspace.path))/\(session).jsonl").path)
        #expect(!FileManager.default.fileExists(atPath: fixture.root.appendingPathComponent(".claude").path))

        // The app's own readers take it as a Claude record of this workspace.
        let listing = ResumableSessions.listing(ResumableSessionQuery(workspacePath: fixture.workspace.path, environment: environment, home: fixture.root, includeAutomated: true), provider: "claude")
        let listed = try #require(listing.items.first { $0.sessionID == session })
        #expect(listed.title == "Delegate the release notes to a child.")
        #expect(listed.requests == 1)
        #expect(listed.model == "claude-fake-1")
        let lines = jsonLines(record)
        #expect(lines.map { $0["type"] as? String } == ["user", "assistant", "user", "assistant"])
        #expect(lines.allSatisfy { $0["sessionId"] as? String == session && $0["cwd"] as? String == fixture.workspace.path })
        #expect(zip(lines.dropFirst(), lines).allSatisfy { $0["parentUuid"] as? String == $1["uuid"] as? String })
        #expect(lines[2]["toolUseResult"] != nil)
    }
}

private func optionValue(_ option: String, _ arguments: [String]) -> String? {
    guard let index = arguments.firstIndex(of: option), index + 1 < arguments.count else { return nil }
    return arguments[index + 1]
}
