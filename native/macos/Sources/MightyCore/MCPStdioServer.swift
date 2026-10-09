import Foundation
import Darwin

/// What one per-pane stdio MCP server offers on top of ``MCPStdioServer``:
/// its name, its tools and how a call runs.
protocol MCPToolSurface: Sendable {
    /// `serverInfo` sent with `initialize`.
    var serverInfo: [String: String] { get }
    /// Server-level instructions sent with `initialize`; nil leaves them out.
    var instructions: String? { get }
    /// The `tools/list` entries.
    var toolDefinitions: [[String: Any]] { get }
    /// Whether a `tools/call` naming `name` reaches ``call(_:arguments:)``.
    /// Any other name is answered with an invalid-params error.
    func accepts(tool name: String) -> Bool
    /// Runs one tool call and returns its `tools/call` result.
    func call(_ name: String, arguments: [String: Any]) -> [String: Any]
}

/// The newline-delimited JSON-RPC 2.0 loop (MCP stdio transport) shared by
/// the app's per-pane MCP servers. Nothing is ever written to stdout except
/// protocol messages.
final class MCPStdioServer: @unchecked Sendable {
    static let latestProtocolVersion = "2025-06-18"
    static let supportedProtocolVersions: Set<String> = ["2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25"]
    static let maxLineBytes = AgentIOWire.maxMessageBytes
    /// How long a closed stdin waits for in-flight tool calls before exiting.
    static let drainSeconds: TimeInterval = 20

    private let surface: any MCPToolSurface
    private let writeLock = NSLock()
    private let inFlight = DispatchGroup()

    init(surface: any MCPToolSurface) { self.surface = surface }

    // MARK: - Transport loop

    /// Serve `input` until it closes, writing responses to `output`.
    /// Tool calls run concurrently; responses are written whole, one per line.
    func run(input: FileHandle, output: FileHandle) {
        let inFD = input.fileDescriptor, outFD = output.fileDescriptor
        var pending = Data(), discarding = false
        var chunk = [UInt8](repeating: 0, count: 65_536)
        while true {
            let count = Darwin.read(inFD, &chunk, chunk.count)
            if count < 0, errno == EINTR { continue }
            guard count > 0 else { break }
            pending.append(contentsOf: chunk[0 ..< count])
            while let newline = pending.firstIndex(of: 10) {
                let line = pending[pending.startIndex ..< newline]
                pending = Data(pending[pending.index(after: newline)...])
                if discarding { discarding = false; continue }
                dispatch(line: line, outFD: outFD)
            }
            if pending.count > Self.maxLineBytes {
                // Too long to be a message: drop it up to its newline and say so once.
                pending.removeAll(); discarding = true
                write(Self.error(id: NSNull(), code: -32700, message: "Message exceeds 1 MB."), fd: outFD)
            }
        }
        if !discarding, !pending.isEmpty { dispatch(line: pending, outFD: outFD) }
        _ = inFlight.wait(timeout: .now() + Self.drainSeconds)
    }

    private func dispatch(line: Data, outFD: Int32) {
        guard let message = parse(line) else { return }
        if case .call(let id, let name, let arguments) = message {
            inFlight.enter()
            DispatchQueue.global(qos: .userInitiated).async { [self] in
                write(Self.result(id: id, surface.call(name, arguments: arguments)), fd: outFD)
                inFlight.leave()
            }
        } else if case .reply(let reply) = message {
            write(reply, fd: outFD)
        }
    }

    private func write(_ object: [String: Any], fd: Int32) {
        guard var data = try? JSONSerialization.data(withJSONObject: object, options: [.sortedKeys, .withoutEscapingSlashes]) else { return }
        data.append(10)
        writeLock.lock(); defer { writeLock.unlock() }
        _ = AgentIOWire.writeAll(fd: fd, data)
    }

    // MARK: - JSON-RPC

    private enum Parsed { case reply([String: Any]), call(id: Any, name: String, arguments: [String: Any]), ignore }

    /// Handle one line synchronously; tool calls included. Returns the response
    /// line, or nil for notifications and blank lines.
    func handle(line: String) -> String? {
        guard let message = parse(Data(line.utf8)) else { return nil }
        let object: [String: Any]
        switch message {
        case .reply(let reply): object = reply
        case .call(let id, let name, let arguments): object = Self.result(id: id, surface.call(name, arguments: arguments))
        case .ignore: return nil
        }
        return (try? JSONSerialization.data(withJSONObject: object, options: [.sortedKeys, .withoutEscapingSlashes])).map { String(decoding: $0, as: UTF8.self) }
    }

    private func parse(_ raw: Data) -> Parsed? {
        var line = raw
        if line.last == 13 { line.removeLast() }
        guard !line.allSatisfy({ $0 == 32 || $0 == 9 }) else { return nil }
        guard let json = try? JSONSerialization.jsonObject(with: line, options: [.fragmentsAllowed]) else { return .reply(Self.error(id: NSNull(), code: -32700, message: "Parse error")) }
        guard let request = json as? [String: Any], request["jsonrpc"] as? String == "2.0", let method = request["method"] as? String else {
            let id = (json as? [String: Any]).flatMap { Self.validID($0["id"]) } ?? NSNull()
            return .reply(Self.error(id: id, code: -32600, message: "Invalid Request"))
        }
        // Without an id it is a notification: never answered, whatever the method.
        guard let rawID = request["id"] else { return .ignore }
        guard let id = Self.validID(rawID) else { return .reply(Self.error(id: NSNull(), code: -32600, message: "Invalid Request")) }
        let params = request["params"] as? [String: Any] ?? [:]
        switch method {
        case "initialize":
            let requested = params["protocolVersion"] as? String ?? ""
            let version = Self.supportedProtocolVersions.contains(requested) ? requested : Self.latestProtocolVersion
            var result: [String: Any] = [
                "protocolVersion": version,
                "capabilities": ["tools": ["listChanged": false]],
                "serverInfo": surface.serverInfo,
            ]
            if let instructions = surface.instructions { result["instructions"] = instructions }
            return .reply(Self.result(id: id, result))
        case "ping":
            return .reply(Self.result(id: id, [:]))
        case "tools/list":
            return .reply(Self.result(id: id, ["tools": surface.toolDefinitions]))
        case "tools/call":
            guard let name = params["name"] as? String, surface.accepts(tool: name) else {
                return .reply(Self.error(id: id, code: -32602, message: "Unknown tool: \(String(describing: params["name"] ?? "none").prefix(80))"))
            }
            if let arguments = params["arguments"], !(arguments is [String: Any]) {
                return .reply(Self.result(id: id, Self.toolError("arguments must be an object.")))
            }
            return .call(id: id, name: name, arguments: params["arguments"] as? [String: Any] ?? [:])
        default:
            return .reply(Self.error(id: id, code: -32601, message: "Method not found: \(method.prefix(80))"))
        }
    }

    private static func validID(_ value: Any?) -> Any? {
        if let text = value as? String { return text }
        if let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() { return number }
        return nil
    }

    private static func result(id: Any, _ result: [String: Any]) -> [String: Any] { ["jsonrpc": "2.0", "id": id, "result": result] }
    private static func error(id: Any, code: Int, message: String) -> [String: Any] { ["jsonrpc": "2.0", "id": id, "error": ["code": code, "message": message]] }
    /// A failed tool call: shown to the model as the call's result, never as a protocol error.
    static func toolError(_ message: String) -> [String: Any] { ["content": [["type": "text", "text": message]], "isError": true] }
}
