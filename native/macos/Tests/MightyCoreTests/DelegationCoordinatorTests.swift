import Foundation
import Testing
@testable import MightyCore

private let appExecutable = URL(fileURLWithPath: "/Applications/MightyClaude.app/Contents/MacOS/MightyClaude")
private let sixTools = ["delegate", "list_children", "child_status", "merge", "follow_up", "discard"]

private func json(_ line: String?) -> [String: Any]? {
    line.flatMap { (try? JSONSerialization.jsonObject(with: Data($0.utf8))) as? [String: Any] }
}

private func toolCall(_ name: String, _ arguments: [String: String], id: Int = 7) -> String {
    let data = try! JSONSerialization.data(withJSONObject: ["jsonrpc": "2.0", "id": id, "method": "tools/call", "params": ["name": name, "arguments": arguments]])
    return String(decoding: data, as: UTF8.self)
}

/// The app side as the coordinator sees it. Records every call, so a test can
/// show which calls reached a pane and which reached none.
private final class FakeDelegationHost: DelegationHost, @unchecked Sendable {
    private let lock = NSLock()
    private var recorded: [String] = []
    var calls: [String] { lock.withLock { recorded } }
    private func note(_ call: String) { lock.withLock { recorded.append(call) } }

    func createPane(_ pane: DelegationChildPane) async -> Bool { note("createPane \(pane.sessionId)"); return true }
    func startRun(sessionId: String, input: String) async -> String? { note("startRun \(sessionId)"); return "run-\(sessionId)" }
    func deliver(_ input: String, to sessionId: String, route: DeliveryRoute) async -> String? { note("deliver \(sessionId) \(route.rawValue)"); return "run-\(sessionId)" }
    func paneState(sessionId: String) async -> DelegationPaneState? {
        note("paneState \(sessionId)")
        return DelegationPaneState(sessionId: sessionId, permissionMode: "auto", folder: "/tmp", runId: "run-\(sessionId)", activity: .finished)
    }
    func stopRun(sessionId: String) async { note("stopRun \(sessionId)") }
}

/// No terminal calls are made here.
private struct NoTerminalCalls: AgentIORequestHandler {
    func handle(_ request: AgentIORequest, binding: PaneMCPBinding) async -> AgentIOResponse { .failure("not under test") }
}

/// Opens once; everyone waiting goes on.
private actor Latch {
    private var isOpen = false
    private var waiters: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        if isOpen { return }
        await withCheckedContinuation { waiters.append($0) }
    }
    func open() { isOpen = true; waiters.forEach { $0.resume() }; waiters.removeAll() }
}

/// The coordinator behind the real app-side socket, with a fake host.
@Suite(.serialized, .delegationLane) struct DelegationCoordinatorTests {
    private struct Fixture {
        let folder: URL
        let socketPath: String
        let bindings = PaneMCPBindingRegistry()
        let host = FakeDelegationHost()
        let store: DelegationFileStore
        let coordinator: DelegationCoordinator
        let server: AgentIOSocketServer
        var location: PaneMCPServerLocation { PaneMCPServerLocation(socketPath: socketPath, executable: appExecutable) }

        /// A profile whose delegation file already holds one running child of "parent".
        init(switchOn: Bool = true) throws {
            folder = try shortTemporaryDirectory()
            socketPath = folder.appendingPathComponent("io.sock").path
            store = DelegationFileStore(directory: folder.appendingPathComponent("profile", isDirectory: true))
            var child = ChildRecord(id: "child-1", parentSessionId: "parent", worktreePath: folder.appendingPathComponent("worktrees/child-1").path,
                                    parentBranch: "main", baseCommit: String(repeating: "a", count: 40), startingMode: "plan", requestKey: "key-1")
            child.apply(.startRun)
            try store.save(DelegationFile(children: [child]))
            coordinator = try DelegationCoordinator(store: store, host: host, worktrees: ChildWorktreeMaker(root: folder.appendingPathComponent("worktrees", isDirectory: true)), isSwitchOn: { switchOn })
            server = AgentIOSocketServer(socketPath: socketPath, bindings: bindings, handler: NoTerminalCalls(), delegation: coordinator)
            try server.start()
        }

        func bind(_ pane: String, provider: String = "claude", kind: String = SessionKind.claude, delegation: Bool = true) -> PaneMCPBinding {
            bindings.bind(agentPaneId: pane, server: location, workspaceId: "ws-1", workspacePath: folder.path, provider: provider, kind: kind, delegation: delegation)
        }

        func send(_ tool: String, _ arguments: [String: String], _ binding: PaneMCPBinding) -> DelegationResponse {
            AgentIOSocketClient.send(DelegationRequest(tool: tool, arguments: arguments), token: binding.token, socketPath: socketPath)
        }

        func fileBytes() throws -> Data { try Data(contentsOf: store.fileURL) }

        func tearDown() { server.stop(); try? FileManager.default.removeItem(at: folder) }
    }

    /// Every argument a tool takes, filled in.
    private static func arguments(_ tool: DelegationToolManifest.Tool) -> [String: String] {
        let values = ["task": "Write the release notes", "mode": "plan", "child": "child-1", "expected_head": String(repeating: "b", count: 40), "text": "Add a summary"]
        return Dictionary(uniqueKeysWithValues: tool.arguments.map { ($0.name, values[$0.name] ?? "x") })
    }

    @Test func theDelegationServerListsExactlyTheSixToolsWithStringArguments() throws {
        let mcp = DelegationMCPServer(environment: [PaneMCPBinding.tokenEnvironmentKey: "fixture-token", PaneMCPBinding.socketEnvironmentKey: "/tmp/fixture.sock"]) { _, _, _ in .failure("unused") }
        let tools = try #require((json(mcp.handle(line: #"{"jsonrpc":"2.0","id":2,"method":"tools/list"}"#))?["result"] as? [String: Any])?["tools"] as? [[String: Any]])
        #expect(tools.compactMap { $0["name"] as? String } == sixTools)
        #expect(DelegationToolManifest.all.map(\.name) == sixTools)
        #expect(Set(sixTools).isDisjoint(with: PaneMCPToolManifest.all.map(\.name)))

        let expected = ["delegate": ["task", "mode"], "list_children": [], "child_status": ["child"], "merge": ["child", "expected_head"], "follow_up": ["child", "text"], "discard": ["child"]]
        for tool in tools {
            let name = try #require(tool["name"] as? String)
            #expect(!(tool["description"] as? String ?? "").isEmpty, "\(name)")
            let schema = try #require(tool["inputSchema"] as? [String: Any])
            #expect(schema["type"] as? String == "object")
            #expect(schema["additionalProperties"] as? Bool == false)
            let properties = try #require(schema["properties"] as? [String: [String: Any]])
            #expect(schema["required"] as? [String] == expected[name], "\(name)")
            #expect(Set(properties.keys) == Set(expected[name] ?? ["?"]), "\(name)")
            #expect(properties.values.allSatisfy { $0["type"] as? String == "string" && !($0["description"] as? String ?? "").isEmpty }, "\(name)")
        }
    }

    @Test func withTheSwitchOnEachToolAnswersWithinTheSocketLimit() async throws {
        #expect(DelegationCoordinator.answerSeconds < Double(AgentIOSocketClient.responseTimeoutSeconds))
        let fixture = try Fixture()
        defer { fixture.tearDown() }
        let parent = fixture.bind("parent")
        let clock = ContinuousClock()
        let noAnswer = DelegationResponse.failure("Mighty Claude did not answer the request.")
        for tool in DelegationToolManifest.all {
            let started = clock.now
            let answer = fixture.send(tool.name, Self.arguments(tool), parent)
            #expect(clock.now - started < .seconds(DelegationCoordinator.answerSeconds), "\(tool.name)")
            // Answered by the coordinator's tool, not by the socket giving up or by a gate.
            #expect(answer.refused != nil || answer.error != nil || answer.children != nil || answer.status != nil || answer.merged != nil || answer.followUp != nil, "\(tool.name)")
            #expect(answer != noAnswer, "\(tool.name)")
            #expect(answer != .failure(DelegationIOHandler.unknownToolMessage(tool.name)), "\(tool.name)")
            #expect(answer != .failure(DelegationIOHandler.detachedMessage), "\(tool.name)")
            #expect(answer.refused != .claudeOnly, "\(tool.name)")
        }
    }

    @Test func discardFromTheParentIsAlwaysRefusedWithDiscardHumanOnlyAndChangesNothing() async throws {
        let fixture = try Fixture()
        defer { fixture.tearDown() }
        let parent = fixture.bind("parent")
        let bytes = try fixture.fileBytes()
        let file = await fixture.coordinator.file
        #expect(file == (try fixture.store.load()))
        #expect(file.children.map(\.id) == ["child-1"])

        // Its own child, a child it does not have, no child at all, extra arguments: always the same refusal.
        for arguments in [["child": "child-1"], ["child": "no-such-child"], [:], ["child": "child-1", "force": "true"]] {
            #expect(fixture.send("discard", arguments, parent) == .refusal(.discardHumanOnly), "\(arguments)")
        }
        // Through the delegation MCP server it is tool data naming that one reason code.
        let mcp = DelegationMCPServer(environment: [PaneMCPBinding.tokenEnvironmentKey: parent.token, PaneMCPBinding.socketEnvironmentKey: fixture.socketPath])
        let result = try #require(json(mcp.handle(line: toolCall("discard", ["child": "child-1"])))?["result"] as? [String: Any])
        #expect(result["isError"] as? Bool == true)
        #expect(result["structuredContent"] as? [String: String] == ["refused": "discard_human_only"])
        let text = try #require((result["content"] as? [[String: Any]])?.first?["text"] as? String)
        #expect(text == "Refused: discard_human_only. Nothing was changed.")

        #expect(try fixture.fileBytes() == bytes)
        #expect(await fixture.coordinator.file == file)
        #expect(fixture.host.calls.isEmpty)
        #expect(!FileManager.default.fileExists(atPath: fixture.folder.appendingPathComponent("worktrees").path))
    }

    @Test func theGatesAnswerBeforeAnyTool() async throws {
        let off = try Fixture(switchOn: false)
        defer { off.tearDown() }
        let attached = off.bind("parent")
        for tool in DelegationToolManifest.all {
            #expect(off.send(tool.name, Self.arguments(tool), attached) == .failure(DelegationIOHandler.detachedMessage), "\(tool.name)")
        }

        let on = try Fixture()
        defer { on.tearDown() }
        let bytes = try on.fileBytes()
        let codex = on.bind("codex", provider: "codex"), shell = on.bind("shell", kind: SessionKind.shell)
        for caller in [codex, shell] {
            for tool in DelegationToolManifest.all {
                #expect(on.send(tool.name, Self.arguments(tool), caller) == .refusal(.claudeOnly), "\(caller.agentPaneId) \(tool.name)")
            }
        }
        let detached = on.bind("detached", delegation: false)
        #expect(on.send("discard", ["child": "child-1"], detached) == .failure(DelegationIOHandler.detachedMessage))
        #expect(on.send("no_such_tool", [:], on.bind("parent")) == .failure(DelegationIOHandler.unknownToolMessage("no_such_tool")))
        #expect(try on.fileBytes() == bytes)
        #expect(off.host.calls.isEmpty && on.host.calls.isEmpty)
    }

    @Test func aCallWithoutExactlyItsArgumentsIsAnsweredAndChangesNothing() async throws {
        let fixture = try Fixture()
        defer { fixture.tearDown() }
        let parent = fixture.bind("parent")
        let bytes = try fixture.fileBytes()
        let merge = fixture.send("merge", ["child": "child-1"], parent)
        #expect(merge.refused == nil)
        #expect(merge.error?.contains("expected_head") == true)
        #expect(fixture.send("delegate", ["task": "x", "mode": "plan", "branch": "main"], parent).error?.contains("task, mode") == true)
        #expect(fixture.send("list_children", ["child": "child-1"], parent).error == "list_children takes no arguments.")
        #expect(try fixture.fileBytes() == bytes)
        #expect(fixture.host.calls.isEmpty)
    }

    @Test func aToolStillWorkingAtTheLimitIsAnsweredOnceAndItsWorkGoesOn() async {
        let gate = Latch(), finished = Latch()
        let late = DelegationResponse.failure("late")
        // The work cannot finish before the gate opens, and the gate opens only after the answer.
        let answer = await DelegationCoordinator.answer(within: 0.2, late: late) {
            await gate.wait()
            await finished.open()
            return .refusal(.storeFull)
        }
        #expect(answer == late)
        await gate.open()
        await finished.wait()

        let quick = await DelegationCoordinator.answer(within: 30, late: late) { .refusal(.notGit) }
        #expect(quick == .refusal(.notGit))
        #expect(DelegationCoordinator.lateMessage("merge", seconds: DelegationCoordinator.answerSeconds).hasPrefix("merge did not finish within 45 seconds."))
    }
}
