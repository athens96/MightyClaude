import Foundation
import Darwin
import Testing
@testable import MightyCore

/// Quick commands through run_in_terminal: full output and exit code in the result.
struct AgentTerminalToolTests {
    @Test func quickCommandReturnsDoneWithFullOutputAndExitCode() async throws {
        let pane = FakeAgentTerminalPane()
        pane.nextOutput = "hello world\n"
        let result = try await AgentTerminalRunner(pane: pane).runInTerminal(command: "echo hello world")
        #expect(result.status == .done)
        #expect(result.output == "hello world\n")
        #expect(result.exitCode == 0)
        #expect(result.signal == nil)
        #expect(!result.outputDropped && !result.moreRemains)
    }

    @Test func quickCommandReturnsNonzeroExitCode() async throws {
        let pane = FakeAgentTerminalPane()
        pane.nextCode = 1
        let result = try await AgentTerminalRunner(pane: pane).runInTerminal(command: "false")
        #expect(result.status == .done)
        #expect(result.exitCode == 1)
    }

    @Test func successiveCommandsGetDistinctHandles() async throws {
        let runner = AgentTerminalRunner(pane: FakeAgentTerminalPane())
        let a = try await runner.runInTerminal(command: "echo a")
        let b = try await runner.runInTerminal(command: "echo b")
        #expect(!a.handle.isEmpty && a.handle != b.handle)
    }

    @Test func realPTYReturnsCombinedOutputAndExitCode() async throws {
        let folder = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: folder) }
        let pane = PTYAgentTerminalPane(workingDirectory: folder)
        let result = try await AgentTerminalRunner(pane: pane).runInTerminal(command: "echo hi; echo oops >&2; exit 3")
        #expect(result.status == .done)
        // The tty turns each newline into CR LF, as any terminal shows it.
        #expect(result.output == "hi\r\noops\r\n")
        #expect(result.exitCode == 3)
        #expect(result.signal == nil)
    }

    @Test func realPTYRunsInTheWorkspaceFolderNotTheAppFolder() async throws {
        let folder = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: folder) }
        let pane = PTYAgentTerminalPane(workingDirectory: folder)
        let result = try await AgentTerminalRunner(pane: pane).runInTerminal(command: "pwd -P")
        #expect(result.output.trimmingCharacters(in: .whitespacesAndNewlines) == folder.path)
        #expect(folder.path != FileManager.default.currentDirectoryPath)
    }

    @Test func handlerRunsInTheBoundPanesWorkspace() async throws {
        let folder = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: folder) }
        let handler = AgentTerminalIOHandler(processes: AgentProcessRegistry(), panes: AgentIOPaneRegistry(), webOpen: silentWebOpenService())
        let response = await handler.handle(AgentIORequest(tool: "run_in_terminal", command: "pwd -P; exit 4"), binding: testPaneBinding(workspacePath: folder.path))
        #expect(response.error == nil)
        #expect(response.status == "done")
        #expect(response.exitCode == 4)
        #expect(response.output?.trimmingCharacters(in: .whitespacesAndNewlines) == folder.path)
    }

    @Test func handlerRejectsEmptyAndOversizedCommands() async {
        let pane = FakeAgentTerminalPane()
        let handler = AgentTerminalIOHandler(processes: AgentProcessRegistry(), panes: AgentIOPaneRegistry(), webOpen: silentWebOpenService()) { _ in pane }
        let empty = await handler.handle(AgentIORequest(tool: "run_in_terminal", command: "  \n"), binding: testPaneBinding())
        let missing = await handler.handle(AgentIORequest(tool: "run_in_terminal"), binding: testPaneBinding())
        let huge = await handler.handle(AgentIORequest(tool: "run_in_terminal", command: String(repeating: "x", count: AgentIOWire.maxCommandBytes + 1)), binding: testPaneBinding())
        #expect(empty.error != nil && missing.error != nil && huge.error != nil)
        #expect(pane.launched.isEmpty)
    }
}

/// Reads newline-delimited responses from the MCP server's stdout pipe.
final class MCPPipeReader: @unchecked Sendable {
    private let fd: Int32
    private var buffer = Data()
    init(_ handle: FileHandle) { fd = handle.fileDescriptor }

    /// The next response line as JSON, waiting at most `timeout` seconds.
    func next(timeout: TimeInterval = 20) async -> [String: Any]? {
        await withCheckedContinuation { continuation in
            DispatchQueue.global().async { [self] in continuation.resume(returning: blockingNext(timeout: timeout)) }
        }
    }

    private func blockingNext(timeout: TimeInterval) -> [String: Any]? {
        let deadline = Date().addingTimeInterval(timeout)
        var chunk = [UInt8](repeating: 0, count: 65_536)
        while true {
            if let newline = buffer.firstIndex(of: 10) {
                let line = buffer[buffer.startIndex ..< newline]
                buffer = Data(buffer[buffer.index(after: newline)...])
                return (try? JSONSerialization.jsonObject(with: line)) as? [String: Any]
            }
            let remaining = deadline.timeIntervalSinceNow
            guard remaining > 0 else { return nil }
            var descriptor = pollfd(fd: fd, events: Int16(POLLIN), revents: 0)
            guard poll(&descriptor, 1, Int32(remaining * 1000)) > 0 else { return nil }
            let count = read(fd, &chunk, chunk.count)
            guard count > 0 else { return nil }
            buffer.append(contentsOf: chunk[0 ..< count])
        }
    }
}

/// A stdio MCP server running on its own thread over a pair of pipes.
struct PipedMCPServer {
    let input = Pipe()
    let output = Pipe()
    let reader: MCPPipeReader
    let finished = DispatchSemaphore(value: 0)

    init(environment: [String: String]) {
        reader = MCPPipeReader(output.fileHandleForReading)
        let server = AgentIOMCPServer(environment: environment)
        let (stdin, stdout, done) = (input.fileHandleForReading, output.fileHandleForWriting, finished)
        Thread { server.run(input: stdin, output: stdout); done.signal() }.start()
    }

    func send(_ object: [String: Any]) throws {
        var data = try JSONSerialization.data(withJSONObject: object)
        data.append(10)
        input.fileHandleForWriting.write(data)
    }

    func sendRaw(_ text: String) { input.fileHandleForWriting.write(Data(text.utf8)) }

    /// Close stdin and report whether the server loop returned.
    func close() -> Bool {
        try? input.fileHandleForWriting.close()
        return finished.wait(timeout: .now() + 25) == .success
    }
}

func toolText(_ response: [String: Any]?) -> (text: String, isError: Bool)? {
    guard let result = response?["result"] as? [String: Any], let content = result["content"] as? [[String: Any]], let text = content.first?["text"] as? String else { return nil }
    return (text, result["isError"] as? Bool ?? false)
}

/// The real stdio server over pipes against the real app-side socket server.
@Suite(.serialized) struct AgentTerminalToolIntegrationTests {
    @Test func mcpServerOverPipesRunsEchoThroughTheRealSocketServer() async throws {
        let folder = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: folder) }
        let socketPath = folder.appendingPathComponent("io.sock").path
        let bindings = PaneMCPBindingRegistry()
        let processes = AgentProcessRegistry()
        let handler = AgentTerminalIOHandler(processes: processes, panes: AgentIOPaneRegistry(), webOpen: silentWebOpenService())
        let server = AgentIOSocketServer(socketPath: socketPath, bindings: bindings, handler: handler)
        try server.start()
        defer { server.stop() }
        let binding = bindings.bind(agentPaneId: "pane-a", server: PaneMCPServerLocation(socketPath: socketPath, executable: URL(fileURLWithPath: "/bin/false")), workspaceId: "ws-1", workspacePath: folder.path, provider: "claude")

        let mcp = PipedMCPServer(environment: binding.environment)
        // 1. initialize
        try mcp.send(["jsonrpc": "2.0", "id": 1, "method": "initialize", "params": ["protocolVersion": "2025-06-18", "capabilities": [:], "clientInfo": ["name": "test", "version": "0"]]])
        let initialized = try #require(await mcp.reader.next())
        let info = try #require(initialized["result"] as? [String: Any])
        #expect(initialized["id"] as? Int == 1)
        #expect(info["protocolVersion"] as? String == "2025-06-18")
        #expect((info["capabilities"] as? [String: Any])?["tools"] != nil)
        #expect((info["serverInfo"] as? [String: Any])?["name"] as? String == PaneMCPBinding.serverName)
        try mcp.send(["jsonrpc": "2.0", "method": "notifications/initialized"])
        // 2. tools/list
        try mcp.send(["jsonrpc": "2.0", "id": 2, "method": "tools/list"])
        let listed = try #require(await mcp.reader.next())
        #expect(listed["id"] as? Int == 2)
        let tools = try #require((listed["result"] as? [String: Any])?["tools"] as? [[String: Any]])
        #expect(tools.compactMap { $0["name"] as? String } == ["run_in_terminal", "read_latest_output", "stop", "open_url"])
        // 3. tools/call run echo hi
        try mcp.send(["jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": ["name": "run_in_terminal", "arguments": ["command": "echo hi"]]])
        let called = try #require(await mcp.reader.next())
        #expect(called["id"] as? Int == 3)
        let run = try #require(toolText(called))
        #expect(!run.isError)
        // Raw PTY output: the tty ends each line with CR LF.
        #expect(run.text.contains("\nhi\r\n"))
        #expect(run.text.contains("exit code: 0"))
        #expect(run.text.contains("status: done"))
        #expect(!run.text.contains(binding.token))
        #expect(mcp.close())

        // 4. a call with a bad token is rejected, and so is a revoked one.
        let forged = PipedMCPServer(environment: [PaneMCPBinding.tokenEnvironmentKey: String(repeating: "0", count: 64), PaneMCPBinding.socketEnvironmentKey: socketPath])
        try forged.send(["jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": ["name": "run_in_terminal", "arguments": ["command": "echo forged"]]])
        let rejected = try #require(toolText(await forged.reader.next()))
        #expect(rejected.isError)
        #expect(rejected.text == AgentIOSocketServer.unknownTokenMessage)
        #expect(forged.close())

        bindings.revoke(agentPaneId: "pane-a")
        let revoked = PipedMCPServer(environment: binding.environment)
        try revoked.send(["jsonrpc": "2.0", "id": 5, "method": "tools/call", "params": ["name": "run_in_terminal", "arguments": ["command": "echo late"]]])
        let late = try #require(toolText(await revoked.reader.next()))
        #expect(late.isError)
        #expect(revoked.close())
        await processes.terminateAll(graceSeconds: 0.2)
    }
}
