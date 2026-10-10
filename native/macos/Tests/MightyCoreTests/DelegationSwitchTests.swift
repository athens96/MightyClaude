import Foundation
import Darwin
import Testing
@testable import MightyCore

private let appExecutable = URL(fileURLWithPath: "/Applications/MightyClaude.app/Contents/MacOS/MightyClaude")

private func delegationBinding(pane: String = "pane-a", token: String = "delegation-fixture-token-0123456789", provider: String = "claude", kind: String = SessionKind.claude, delegation: Bool, socketPath: String = "/tmp/mighty.sock") -> PaneMCPBinding {
    PaneMCPBinding(agentPaneId: pane, token: token, server: PaneMCPServerLocation(socketPath: socketPath, executable: appExecutable), workspaceId: "ws-1", workspacePath: "/tmp", provider: provider, kind: kind, delegation: delegation)
}

/// The `mcpServers` object of a Claude run's `--mcp-config`.
private func claudeServers(_ arguments: [String]) throws -> [String: [String: Any]] {
    #expect(arguments.filter { $0 == "--mcp-config" }.count == 1)
    let config = try #require(arguments.firstIndex(of: "--mcp-config").map { arguments[$0 + 1] })
    return try #require((try JSONSerialization.jsonObject(with: Data(config.utf8)) as? [String: Any])?["mcpServers"] as? [String: [String: Any]])
}

/// The server names a Codex run's `-c mcp_servers.<name>.*` flags give it.
private func codexServers(_ arguments: [String]) -> Set<String> {
    Set(arguments.filter { $0.hasPrefix("mcp_servers.") }.compactMap { $0.split(separator: ".").dropFirst().first.map(String.init) })
}

private func json(_ line: String?) -> [String: Any]? {
    line.flatMap { (try? JSONSerialization.jsonObject(with: Data($0.utf8))) as? [String: Any] }
}

private func toolCall(_ name: String, _ arguments: Any, id: Int = 5) -> String {
    let data = try! JSONSerialization.data(withJSONObject: ["jsonrpc": "2.0", "id": id, "method": "tools/call", "params": ["name": name, "arguments": arguments]])
    return String(decoding: data, as: UTF8.self)
}

private func errorCode(_ response: [String: Any]?) -> Int? { (response?["error"] as? [String: Any])?["code"] as? Int }

/// The hidden switch: one defaults key, off unless it is set.
struct DelegationSwitchTests {
    @Test func theSwitchIsOffUntilTheHiddenKeyTurnsItOn() throws {
        let suite = "dev.mightyclaude.tests.delegation.\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        #expect(DelegationSwitch.defaultsKey == "delegation.enabled")
        #expect(!DelegationSwitch.isOn(defaults))
        defaults.set(true, forKey: DelegationSwitch.defaultsKey)
        #expect(DelegationSwitch.isOn(defaults))
        defaults.set(false, forKey: DelegationSwitch.defaultsKey)
        #expect(!DelegationSwitch.isOn(defaults))
        defaults.removeObject(forKey: DelegationSwitch.defaultsKey)
        #expect(!DelegationSwitch.isOn(defaults))
    }

    @Test func onlyAnAgentPaneRunningClaudeIsAClaudePane() {
        #expect(DelegationSwitch.isClaudePane(kind: SessionKind.claude, provider: "claude"))
        #expect(!DelegationSwitch.isClaudePane(kind: SessionKind.claude, provider: "codex"))
        #expect(!DelegationSwitch.isClaudePane(kind: SessionKind.claude, provider: "gemini"))
        #expect(!DelegationSwitch.isClaudePane(kind: SessionKind.shell, provider: "claude"))
        #expect(!DelegationSwitch.isClaudePane(kind: SessionKind.browser, provider: "claude"))
    }

    /// The refusal vocabulary is the ontology's ReasonCode list, exactly.
    @Test func reasonCodesAreTheOntologyNames() {
        #expect(DelegationReasonCode.allCases.map(\.rawValue) == [
            "claude_only", "child_cannot_delegate", "width_cap", "low_disk", "store_full", "not_git", "unborn_branch", "detached_head",
            "wider_mode", "discard_human_only", "not_reported", "tracked_changes", "head_moved", "diverged", "branch_not_checked_out",
            "child_closed", "parent_closed", "follow_up_limit", "worktree_missing", "merge_conflict", "undo_parent_moved", "parent_busy",
            "workspace_has_children", "held_not_removable",
        ])
    }
}

/// What a run's MCP flags name, by switch and pane.
struct DelegationAttachmentTests {
    private let plugin = URL(fileURLWithPath: "/tmp", isDirectory: true)

    @Test func switchedOffAClaudeConfigIsByteForByteTheOneBefore() throws {
        let binding = delegationBinding(delegation: false)
        let args = try ProviderService.arguments(StartRunRequest(sessionId: "a", workspaceId: "w", input: "hi"), pluginDirectory: plugin, paneMCPBinding: binding)
        let config = try #require(args.firstIndex(of: "--mcp-config").map { args[$0 + 1] })
        #expect(config == "{\"mcpServers\":{\"mighty-terminal\":{\"args\":[\"--agent-io-mcp\"],\"command\":\"\(appExecutable.path)\",\"type\":\"stdio\"}}}")
        #expect(!args.joined(separator: " ").contains(DelegationMCPServer.serverName))
        #expect(!args.contains(DelegationMCPServer.headlessArgument))
    }

    @Test func switchedOnAClaudePaneGetsTheDelegationServerBesideTheTerminalServer() throws {
        let binding = delegationBinding(delegation: true)
        let args = try ProviderService.arguments(StartRunRequest(sessionId: "a", workspaceId: "w", input: "hi"), pluginDirectory: plugin, paneMCPBinding: binding)
        #expect(!args.contains("--strict-mcp-config"))
        #expect(!args.joined(separator: " ").contains(binding.token))
        let servers = try claudeServers(args)
        #expect(Set(servers.keys) == [PaneMCPBinding.serverName, DelegationMCPServer.serverName])
        let terminal = try #require(servers[PaneMCPBinding.serverName])
        #expect(terminal["args"] as? [String] == [PaneMCPServerLocation.headlessArgument])
        let delegation = try #require(servers[DelegationMCPServer.serverName])
        #expect(delegation["type"] as? String == "stdio")
        #expect(delegation["command"] as? String == appExecutable.path)
        #expect(delegation["args"] as? [String] == [DelegationMCPServer.headlessArgument])
        // The token and socket reach it through the inherited environment, as for mighty-terminal.
        #expect(delegation["env"] == nil)
    }

    @Test func aCodexRunNeverNamesTheDelegationServer() throws {
        let home = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: home) }
        let request = StartRunRequest(sessionId: "b", workspaceId: "w", input: "hi", provider: "codex")
        // Even a binding that claims the attachment adds nothing to a Codex run.
        for attached in [false, true] {
            let args = try ProviderService.arguments(request, pluginDirectory: plugin, paneMCPBinding: delegationBinding(pane: "b", provider: "codex", delegation: attached), codexHome: home)
            #expect(codexServers(args) == [PaneMCPBinding.serverName])
            #expect(!args.joined(separator: " ").contains(DelegationMCPServer.serverName))
            #expect(!args.joined(separator: " ").contains(DelegationMCPServer.headlessArgument))
        }
    }
}

/// Settable from the test while a runner reads it.
private final class SwitchFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var on = false
    var value: Bool {
        get { lock.lock(); defer { lock.unlock() }; return on }
        set { lock.lock(); on = newValue; lock.unlock() }
    }
}

/// The real run path: ProcessRunner launches fake Claude and Codex CLIs that
/// write down the arguments they were started with.
@Suite(.serialized) struct DelegationRunPathTests {
    private func executable(_ url: URL, _ source: String) throws {
        try source.write(to: url, atomically: true, encoding: .utf8)
        guard chmod(url.path, 0o755) == 0 else { throw MightyError("chmod failed") }
    }

    /// The argv a fake CLI wrote in `folder`, one argument per line.
    private func recordedArguments(in folder: URL) async throws -> [String] {
        let file = folder.appendingPathComponent("seen-argv")
        let deadline = Date().addingTimeInterval(15)
        while !FileManager.default.fileExists(atPath: file.path), Date() < deadline { try await Task.sleep(nanoseconds: 20_000_000) }
        return try String(contentsOf: file, encoding: .utf8).split(separator: "\n", omittingEmptySubsequences: false).map(String.init)
    }

    @Test func theSwitchDecidesAndOnlyAClaudePaneGetsTheDelegationServer() async throws {
        let root = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: root) }
        let claude = root.appendingPathComponent("claude"), codex = root.appendingPathComponent("codex")
        try executable(claude, """
        #!/bin/sh
        if [ "$1" = '--version' ]; then printf '2.1.271 (Claude Code)\\n'; exit 0; fi
        # Only the agent run carries our servers; the model catalog probe does not.
        case "$*" in *\(PaneMCPBinding.serverName)*)
          printf '%s\\n' "$@" > "$PWD/seen-argv.tmp" && mv "$PWD/seen-argv.tmp" "$PWD/seen-argv";;
        esac
        printf '{"type":"result","subtype":"success","result":"ok"}\\n'
        """)
        try executable(codex, """
        #!/bin/sh
        if [ "$1" = '--version' ]; then printf 'codex-cli 0.153.4\\n'; exit 0; fi
        for argument in "$@"; do if [ "$argument" = app-server ]; then exit 3; fi; done
        /bin/cat > /dev/null
        printf '%s\\n' "$@" > "$PWD/seen-argv.tmp" && mv "$PWD/seen-argv.tmp" "$PWD/seen-argv"
        printf '{"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}\\n'
        """)
        let plugin = root.appendingPathComponent("plugin", isDirectory: true)
        try FileManager.default.createDirectory(at: plugin.appendingPathComponent(".claude-plugin"), withIntermediateDirectories: true)
        try Data("{\"name\":\"mighty\"}".utf8).write(to: plugin.appendingPathComponent(".claude-plugin/plugin.json"))
        // One folder per run, so each fake writes its own seen-argv.
        var workspaces: [String: Workspace] = [:]
        for name in ["claude-off", "claude-on", "codex-on", "shell-on"] {
            let folder = root.appendingPathComponent(name, isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            workspaces[name] = Workspace(id: "ws-\(name)", name: name, path: folder.path)
        }
        // A busy CI runner may take longer than the app's 4 s for the fake's --version.
        let service = ProviderService(binaryOverrides: ["claude": claude, "codex": codex], environment: ["PATH": "/usr/bin:/bin:/usr/sbin:/sbin", "HOME": root.path], versionProbeTimeout: 60)
        let bindings = PaneMCPBindingRegistry()
        let location = PaneMCPServerLocation(socketPath: root.appendingPathComponent("io.sock").path, executable: appExecutable)
        let switchFlag = SwitchFlag()
        let runner = ProcessRunner(providerService: service, pluginDirectory: plugin, paneMCPServer: location, paneMCPBindings: bindings, delegationEnabled: { switchFlag.value }, onEvent: { _ in })
        func start(_ name: String, kind: String = SessionKind.claude, provider: String = "claude", input: String = "hi") async throws {
            let workspace = try #require(workspaces[name])
            try await runner.start(request: StartRunRequest(sessionId: name, workspaceId: workspace.id, kind: kind, input: input, provider: provider), workspace: workspace)
        }
        do {
            // Off, the default: the Claude pane's run names mighty-terminal alone.
            try await start("claude-off")
            let off = try claudeServers(try await recordedArguments(in: URL(fileURLWithPath: workspaces["claude-off"]!.path)))
            #expect(Array(off.keys) == [PaneMCPBinding.serverName])
            #expect(bindings.binding(forPane: "claude-off")?.delegation == false)

            // On: a Claude pane's run also gets the delegation server; a Codex pane's does not.
            switchFlag.value = true
            try await start("claude-on")
            let onArguments = try await recordedArguments(in: URL(fileURLWithPath: workspaces["claude-on"]!.path))
            let on = try claudeServers(onArguments)
            #expect(Set(on.keys) == [PaneMCPBinding.serverName, DelegationMCPServer.serverName])
            #expect(on[DelegationMCPServer.serverName]?["args"] as? [String] == [DelegationMCPServer.headlessArgument])
            let claudeBinding = try #require(bindings.binding(forPane: "claude-on"))
            #expect(claudeBinding.delegation && claudeBinding.kind == SessionKind.claude && claudeBinding.provider == "claude")
            #expect(!onArguments.joined(separator: "\n").contains(claudeBinding.token))

            try await start("codex-on", provider: "codex")
            let codexArguments = try await recordedArguments(in: URL(fileURLWithPath: workspaces["codex-on"]!.path))
            #expect(codexServers(codexArguments) == [PaneMCPBinding.serverName])
            #expect(!codexArguments.joined(separator: "\n").contains(DelegationMCPServer.serverName))
            #expect(bindings.binding(forPane: "codex-on")?.delegation == false)

            // A shell pane gets no MCP server at all, so no token either.
            try await start("shell-on", kind: SessionKind.shell, input: "true")
            #expect(bindings.binding(forPane: "shell-on") == nil)
        } catch { await runner.shutdown(); await service.shutdown(); throw error }
        await runner.shutdown(); await service.shutdown()
    }
}

/// Terminal calls seen by the app, so a delegation call can be shown not to reach them.
private final class RecordingTerminalHandler: AgentIORequestHandler, @unchecked Sendable {
    private let lock = NSLock()
    private var panes: [String] = []
    var seen: [String] { lock.lock(); defer { lock.unlock() }; return panes }
    func handle(_ request: AgentIORequest, binding: PaneMCPBinding) async -> AgentIOResponse {
        lock.withLock { panes.append(binding.agentPaneId) }
        return AgentIOResponse(handle: "h", status: "done", exitCode: 0, output: binding.agentPaneId)
    }
}

/// Delegation calls through the real app-side socket server.
@Suite(.serialized) struct DelegationGateTests {
    private static let delegate = DelegationRequest(tool: "delegate", arguments: ["task": "Write the release notes", "mode": "plan"])

    private struct Fixture {
        let folder: URL
        let socketPath: String
        let bindings = PaneMCPBindingRegistry()
        let terminal = RecordingTerminalHandler()
        let server: AgentIOSocketServer
        var location: PaneMCPServerLocation { PaneMCPServerLocation(socketPath: socketPath, executable: appExecutable) }

        init(delegation: (any DelegationRequestHandler)?) throws {
            folder = try shortTemporaryDirectory()
            socketPath = folder.appendingPathComponent("io.sock").path
            server = AgentIOSocketServer(socketPath: socketPath, bindings: bindings, handler: terminal, delegation: delegation)
            try server.start()
        }

        func bind(_ pane: String, provider: String, kind: String = SessionKind.claude, delegation: Bool = false) -> PaneMCPBinding {
            bindings.bind(agentPaneId: pane, server: location, workspaceId: "ws-1", workspacePath: folder.path, provider: provider, kind: kind, delegation: delegation)
        }

        func send(_ request: DelegationRequest, _ binding: PaneMCPBinding) -> DelegationResponse { AgentIOSocketClient.send(request, token: binding.token, socketPath: socketPath) }

        /// The files beside the socket, the live panes and their tokens.
        func state() throws -> [String] {
            try FileManager.default.contentsOfDirectory(atPath: folder.path).sorted() + bindings.activePaneIds.map { "\($0)=\(bindings.binding(forPane: $0)?.token ?? "")" }
        }

        func tearDown() { server.stop(); try? FileManager.default.removeItem(at: folder) }
    }

    @Test func switchedOnANonClaudePaneIsRefusedWithClaudeOnlyAndNothingChanges() async throws {
        let fixture = try Fixture(delegation: DelegationIOHandler(isSwitchOn: { true }))
        defer { fixture.tearDown() }
        let codex = fixture.bind("codex", provider: "codex")
        let gemini = fixture.bind("gemini", provider: "gemini")
        let shell = fixture.bind("shell", provider: "claude", kind: SessionKind.shell)
        let claude = fixture.bind("claude", provider: "claude", delegation: true)
        let before = try fixture.state()
        for caller in [codex, gemini, shell] {
            // Whatever delegation tool it names, the one answer is claude_only.
            for tool in ["delegate", "list_children", "child_status", "merge", "follow_up", "discard", "no_such_tool"] {
                let request = tool == "delegate" ? Self.delegate : DelegationRequest(tool: tool, arguments: ["child": "child-1"])
                #expect(fixture.send(request, caller) == .refusal(.claudeOnly), "\(caller.agentPaneId) \(tool)")
            }
        }
        // A Claude pane with the server attached is not refused as a non-Claude pane.
        #expect(fixture.send(Self.delegate, claude).refused == nil)
        #expect(try fixture.state() == before)
        #expect(fixture.terminal.seen.isEmpty)
    }

    @Test func throughTheDelegationServerTheRefusalIsToolDataWithOneReasonCode() async throws {
        let fixture = try Fixture(delegation: DelegationIOHandler(isSwitchOn: { true }))
        defer { fixture.tearDown() }
        let codex = fixture.bind("codex", provider: "codex")
        let before = try fixture.state()
        let mcp = DelegationMCPServer(environment: [PaneMCPBinding.tokenEnvironmentKey: codex.token, PaneMCPBinding.socketEnvironmentKey: fixture.socketPath])
        let result = try #require(json(mcp.handle(line: toolCall("delegate", ["task": "Write the release notes", "mode": "plan"])))?["result"] as? [String: Any])
        #expect(result["isError"] as? Bool == true)
        #expect(result["structuredContent"] as? [String: String] == ["refused": "claude_only"])
        let text = try #require((result["content"] as? [[String: Any]])?.first?["text"] as? String)
        #expect(text == "Refused: claude_only. Nothing was changed.")
        #expect(DelegationReasonCode.allCases.filter { text.contains($0.rawValue) } == [.claudeOnly])
        #expect(try fixture.state() == before)
    }

    @Test func switchedOffEveryDelegationCallReachesNothing() async throws {
        let fixture = try Fixture(delegation: DelegationIOHandler(isSwitchOn: { false }))
        defer { fixture.tearDown() }
        let before = try fixture.state()
        for binding in [fixture.bind("codex", provider: "codex"), fixture.bind("claude", provider: "claude", delegation: true)] {
            #expect(fixture.send(Self.delegate, binding) == .failure(DelegationIOHandler.detachedMessage))
        }
        #expect(fixture.terminal.seen.isEmpty)
        #expect(try fixture.state().count == before.count + 2)
    }

    @Test func aClaudeRunStartedWhileTheSwitchWasOffStaysDetached() async throws {
        let fixture = try Fixture(delegation: DelegationIOHandler(isSwitchOn: { true }))
        defer { fixture.tearDown() }
        let claude = fixture.bind("claude", provider: "claude", delegation: false)
        #expect(fixture.send(Self.delegate, claude) == .failure(DelegationIOHandler.detachedMessage))
    }

    @Test func forgedAndRevokedTokensReachNoPane() async throws {
        let fixture = try Fixture(delegation: DelegationIOHandler(isSwitchOn: { true }))
        defer { fixture.tearDown() }
        let claude = fixture.bind("claude", provider: "claude", delegation: true)
        #expect(AgentIOSocketClient.send(Self.delegate, token: "forged", socketPath: fixture.socketPath) == .failure(AgentIOSocketServer.unknownTokenMessage))
        fixture.bindings.revoke(agentPaneId: "claude")
        #expect(fixture.send(Self.delegate, claude) == .failure(AgentIOSocketServer.unknownTokenMessage))
    }

    @Test func terminalCallsOnTheSharedSocketAreServedAsBefore() async throws {
        // An app without a delegation handler has no delegation server to reach.
        let fixture = try Fixture(delegation: nil)
        defer { fixture.tearDown() }
        let claude = fixture.bind("claude", provider: "claude", delegation: true)
        #expect(fixture.send(Self.delegate, claude) == .failure(DelegationIOHandler.detachedMessage))
        let terminal = AgentIOSocketClient.send(AgentIORequest(tool: "read_latest_output", handle: "h"), token: claude.token, socketPath: fixture.socketPath)
        #expect(terminal.output == "claude")
        #expect(fixture.terminal.seen == ["claude"])
    }
}

/// Records what the delegation server forwards and answers with a canned response.
private final class RecordingDelegationTransport: @unchecked Sendable {
    private let lock = NSLock()
    private var recorded: [(DelegationRequest, String, String)] = []
    var reply = DelegationResponse.refusal(.claudeOnly)
    var calls: [(request: DelegationRequest, token: String, socketPath: String)] { lock.lock(); defer { lock.unlock() }; return recorded }
    func send(_ request: DelegationRequest, token: String, socketPath: String) -> DelegationResponse {
        lock.lock(); recorded.append((request, token, socketPath)); lock.unlock()
        return reply
    }
}

/// The `--agent-delegation-mcp` stdio server: a separate server on the pane's token.
struct DelegationServerSurfaceTests {
    private let environment = [PaneMCPBinding.tokenEnvironmentKey: "fixture-token", PaneMCPBinding.socketEnvironmentKey: "/tmp/fixture.sock"]

    private func server(_ transport: RecordingDelegationTransport, environment: [String: String]? = nil) -> DelegationMCPServer {
        DelegationMCPServer(environment: environment ?? self.environment) { transport.send($0, token: $1, socketPath: $2) }
    }

    @Test func initializeNamesTheDelegationServerNotMightyTerminal() throws {
        let response = json(server(RecordingDelegationTransport()).handle(line: #"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}"#))
        let result = try #require(response?["result"] as? [String: Any])
        #expect(result["protocolVersion"] as? String == "2025-03-26")
        #expect((result["serverInfo"] as? [String: String])?["name"] == DelegationMCPServer.serverName)
        #expect(DelegationMCPServer.serverName != PaneMCPBinding.serverName)
        #expect((result["capabilities"] as? [String: Any])?["tools"] != nil)
    }

    @Test func theTerminalToolsStayOnMightyTerminalAlone() throws {
        let list = #"{"jsonrpc":"2.0","id":2,"method":"tools/list"}"#
        let delegationTools = try #require((json(server(RecordingDelegationTransport()).handle(line: list))?["result"] as? [String: Any])?["tools"] as? [[String: Any]])
        let terminalTools = try #require((json(AgentIOMCPServer(environment: environment) { _, _, _ in AgentIOResponse() }.handle(line: list))?["result"] as? [String: Any])?["tools"] as? [[String: Any]])
        #expect(terminalTools.compactMap { $0["name"] as? String } == ["run_in_terminal", "read_latest_output", "stop", "open_url"])
        #expect(Set(delegationTools.compactMap { $0["name"] as? String }).isDisjoint(with: PaneMCPToolManifest.all.map(\.name)))
    }

    @Test func aCallIsForwardedWithItsStringArgumentsAndThePaneToken() throws {
        let transport = RecordingDelegationTransport()
        let mcp = server(transport)
        let refused = try #require(json(mcp.handle(line: toolCall("delegate", ["task": "Write the release notes", "mode": "plan"])))?["result"] as? [String: Any])
        #expect(refused["isError"] as? Bool == true)
        #expect(refused["structuredContent"] as? [String: String] == ["refused": "claude_only"])
        let call = try #require(transport.calls.first)
        #expect(call.request == DelegationRequest(tool: "delegate", arguments: ["task": "Write the release notes", "mode": "plan"]))
        #expect(call.token == "fixture-token" && call.socketPath == "/tmp/fixture.sock")

        transport.reply = .failure(DelegationIOHandler.detachedMessage)
        let failed = try #require(json(mcp.handle(line: toolCall("list_children", [:])))?["result"] as? [String: Any])
        #expect(failed["isError"] as? Bool == true)
        #expect(failed["structuredContent"] == nil)
        #expect((failed["content"] as? [[String: Any]])?.first?["text"] as? String == DelegationIOHandler.detachedMessage)
    }

    @Test func malformedCallsNeverReachTheApp() {
        let transport = RecordingDelegationTransport()
        let mcp = server(transport)
        for name in ["Delegate", "../delegate", "", String(repeating: "a", count: 65), "1delegate"] {
            #expect(errorCode(json(mcp.handle(line: toolCall(name, [:])))) == -32602, "\(name)")
        }
        for arguments in [["task": 42], ["task": ["a"]], ["Task": "x"]] as [[String: Any]] {
            let result = json(mcp.handle(line: toolCall("delegate", arguments)))?["result"] as? [String: Any]
            #expect(result?["isError"] as? Bool == true, "\(arguments)")
        }
        #expect((json(mcp.handle(line: toolCall("delegate", "not an object")))?["result"] as? [String: Any])?["isError"] as? Bool == true)
        let detached = json(server(transport, environment: [:]).handle(line: toolCall("delegate", ["task": "x", "mode": "plan"])))?["result"] as? [String: Any]
        #expect(detached?["isError"] as? Bool == true)
        #expect(transport.calls.isEmpty)
    }

    @Test func theServerLoopAnswersOverPipesAndExitsWhenStdinCloses() async throws {
        let input = Pipe(), output = Pipe(), finished = DispatchSemaphore(value: 0)
        let reader = MCPPipeReader(output.fileHandleForReading)
        let mcp = server(RecordingDelegationTransport())
        let (stdin, stdout) = (input.fileHandleForReading, output.fileHandleForWriting)
        Thread { mcp.run(input: stdin, output: stdout); finished.signal() }.start()
        input.fileHandleForWriting.write(Data("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\"}\n".utf8))
        let pong = try #require(await reader.next())
        #expect(pong["id"] as? Int == 3 && pong["result"] != nil)
        try input.fileHandleForWriting.close()
        #expect(finished.wait(timeout: .now() + 25) == .success)
    }
}
