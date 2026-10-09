import Foundation

/// The delegation MCP server: the app binary run as `--agent-delegation-mcp`.
///
/// A separate server from mighty-terminal, so that one keeps exactly its four
/// tools. A run gets it only in a Claude pane and only while the hidden
/// ``DelegationSwitch`` is on, and it lists exactly the six tools of
/// ``DelegationToolManifest``. It shares that pane's token and socket: each
/// `tools/call` is forwarded to the app with `MIGHTY_PANE_TOKEN` over
/// `MIGHTY_AGENT_IO_SOCKET`, and the app decides who may call them and runs
/// them (``DelegationCoordinator``). Every call answers within the socket's
/// 60 s limit. The token is never written anywhere.
public final class DelegationMCPServer: @unchecked Sendable {
    public static let serverName = "mighty-delegation"
    public static let headlessArgument = "--agent-delegation-mcp"

    public typealias Transport = @Sendable (DelegationRequest, _ token: String, _ socketPath: String) -> DelegationResponse

    private let core: MCPStdioServer

    public init(environment: [String: String] = ProcessInfo.processInfo.environment, transport: @escaping Transport = { AgentIOSocketClient.send($0, token: $1, socketPath: $2) }) {
        let token = environment[PaneMCPBinding.tokenEnvironmentKey].flatMap { $0.isEmpty ? nil : $0 }
        let socketPath = environment[PaneMCPBinding.socketEnvironmentKey].flatMap { $0.isEmpty ? nil : $0 }
        core = MCPStdioServer(surface: DelegationToolSurface(token: token, socketPath: socketPath, transport: transport))
    }

    /// Serve `input` until it closes, writing responses to `output`.
    public func run(input: FileHandle, output: FileHandle) { core.run(input: input, output: output) }

    /// Handle one line synchronously; tool calls included. Returns the response
    /// line, or nil for notifications and blank lines.
    public func handle(line: String) -> String? { core.handle(line: line) }

    /// The `tools/call` result for the app's answer. A refusal names its one
    /// reason code in the text and as structured content; a child is tool
    /// data in both too.
    static func result(_ response: DelegationResponse) -> [String: Any] {
        if let reason = response.refused {
            return ["content": [["type": "text", "text": "Refused: \(reason.rawValue). Nothing was changed."]], "structuredContent": ["refused": reason.rawValue], "isError": true]
        }
        if let child = response.child {
            let text = "Child \(child.id) is \(child.state.rawValue), on the branch \(child.branch), starting in \(child.startingMode) mode."
            let data = ["id": child.id, "state": child.state.rawValue, "branch": child.branch, "mode": child.startingMode]
            return ["content": [["type": "text", "text": text]], "structuredContent": ["child": data]]
        }
        return MCPStdioServer.toolError(response.error ?? "Mighty Claude did not answer the request.")
    }
}

/// Lists the six delegation tools and forwards every well-formed call to the
/// app, which decides whether this pane may use them.
private struct DelegationToolSurface: MCPToolSurface {
    let token: String?
    let socketPath: String?
    let transport: DelegationMCPServer.Transport

    var serverInfo: [String: String] { ["name": DelegationMCPServer.serverName, "title": "Mighty Claude delegation", "version": "1.0.0"] }
    var instructions: String? { nil }
    var toolDefinitions: [[String: Any]] { DelegationToolManifest.all.map(DelegationToolManifest.definition) }
    func accepts(tool name: String) -> Bool { DelegationRequest.isToolName(name) }

    func call(_ name: String, arguments: [String: Any]) -> [String: Any] {
        var values: [String: String] = [:]
        for (key, value) in arguments {
            guard DelegationRequest.isToolName(key), let text = value as? String else { return MCPStdioServer.toolError("Arguments of \(name) must be strings with lowercase names.") }
            values[key] = text
        }
        guard let token, let socketPath else { return MCPStdioServer.toolError("This MCP server was started without a Mighty Claude pane connection, so it cannot reach the app.") }
        return DelegationMCPServer.result(transport(DelegationRequest(tool: name, arguments: values), token, socketPath))
    }
}
