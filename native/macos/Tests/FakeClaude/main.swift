import Foundation
import MightyCore

// A scripted stand-in for the `claude` CLI, for tests and CI only. There is
// no model: what the "agent" does is read from a per-test script.
//
// The app launches it through its normal run path. It speaks the CLI's
// stream-json protocol, answers the host's `initialize` control request,
// raises `can_use_tool` permission requests in the CLI's format, reaches the
// MCP servers named in the app-written `--mcp-config` with the environment it
// inherited (so the pane token never leaves the environment), and writes its
// transcript where Claude Code does: `<CLAUDE_CONFIG_DIR>/projects/<cwd with
// every character but ASCII letters and digits as "-">/<session id>.jsonl`.
//
// The same binary also runs the app's two stdio MCP servers, exactly as the
// app binary's headless `--agent-io-mcp` and `--agent-delegation-mcp` modes do,
// so a test can point the app-written config at it and still reach the real
// servers.
//
// Environment:
//   FAKE_CLAUDE_SCRIPT  JSON file: {"sessionId"?, "model"?, "turns": [[step]]}.
//                       Turn n is played for the n-th user message.
//   FAKE_CLAUDE_LOG     optional JSON-lines file of what the fake saw and did.
//
// Steps, played in order:
//   {"say": "text"}                              an assistant text message
//   {"call": "tool", "arguments": {...},         a tools/call on `server`
//    "server": "mighty-delegation", "ask": true}  (default mighty-delegation),
//                                                asked for first when `ask`
//   {"permission": "Bash", "input": {...}}       a built-in tool use, asked for
//   {"write": "REPORT.md", "text": "..."}        a file under the working folder
//   {"sleep": 0.5}                               seconds
//   {"exit": 3}                                  leave at once with this code

let arguments = CommandLine.arguments
if arguments.contains(DelegationMCPServer.headlessArgument) {
    signal(SIGPIPE, SIG_IGN)
    DelegationMCPServer().run(input: .standardInput, output: .standardOutput)
    exit(0)
}
if arguments.contains(PaneMCPServerLocation.headlessArgument) {
    signal(SIGPIPE, SIG_IGN)
    AgentIOMCPServer().run(input: .standardInput, output: .standardOutput)
    exit(0)
}
if arguments.dropFirst().first == "--version" {
    print("2.1.271 (Claude Code)")
    exit(0)
}
// The app's model-list probe: leaving at once makes it use its built-in list.
if arguments.contains("--no-session-persistence") { exit(0) }
signal(SIGPIPE, SIG_IGN)
FakeClaude(arguments: Array(arguments.dropFirst()), environment: ProcessInfo.processInfo.environment).run()

struct Step: Decodable {
    var say: String?
    var call: String?
    var server: String?
    var arguments: [String: String]?
    var ask: Bool?
    var permission: String?
    var input: [String: String]?
    var write: String?
    var text: String?
    var sleep: Double?
    var exit: Int32?
}

struct Script: Decodable {
    var sessionId: String?
    var model: String?
    var turns: [[Step]]
}

/// Lines from stdin, read on their own thread so a step can wait for one
/// answer while other messages queue up.
final class Inbox: @unchecked Sendable {
    private let condition = NSCondition()
    private var messages: [[String: Any]] = []
    private var ended = false

    func start(streamJSON: Bool) {
        Thread.detachNewThread { [self] in
            if streamJSON {
                while let line = readLine(strippingNewline: true) {
                    guard let object = (try? JSONSerialization.jsonObject(with: Data(line.utf8))) as? [String: Any] else { continue }
                    condition.lock(); messages.append(object); condition.signal(); condition.unlock()
                }
            } else {
                // Plain input is one prompt, whole.
                let prompt = String(decoding: FileHandle.standardInput.readDataToEndOfFile(), as: UTF8.self)
                condition.lock(); messages.append(["type": "user", "message": ["role": "user", "content": prompt]]); condition.unlock()
            }
            condition.lock(); ended = true; condition.broadcast(); condition.unlock()
        }
    }

    /// The first queued message `matching` accepts, waiting for one; nil once
    /// stdin has closed with none left.
    func next(_ matching: ([String: Any]) -> Bool = { _ in true }) -> [String: Any]? {
        condition.lock(); defer { condition.unlock() }
        while true {
            if let index = messages.firstIndex(where: matching) { return messages.remove(at: index) }
            if ended { return nil }
            condition.wait()
        }
    }
}

/// One stdio MCP server from the config, spoken to over JSON-RPC lines.
final class MCPConnection {
    let name: String
    private let process = Process()
    private let input = Pipe(), output = Pipe()
    private var buffer = Data()
    private var nextId = 1
    private(set) var tools: [String] = []

    init(name: String, command: String, arguments: [String]) throws {
        self.name = name
        process.executableURL = URL(fileURLWithPath: command)
        process.arguments = arguments
        // Inherited environment, as Claude Code starts stdio servers.
        process.standardInput = input; process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        try process.run()
        guard request("initialize", ["protocolVersion": "2025-06-18", "capabilities": [:] as [String: Any],
                                     "clientInfo": ["name": "claude-code", "version": "2.1.271"]])?["result"] != nil else {
            throw NSError(domain: "FakeClaude", code: 1, userInfo: [NSLocalizedDescriptionKey: "\(name) did not initialize"])
        }
        send(["jsonrpc": "2.0", "method": "notifications/initialized"])
        let listed = (request("tools/list", [:])?["result"] as? [String: Any])?["tools"] as? [[String: Any]] ?? []
        tools = listed.compactMap { $0["name"] as? String }
    }

    /// The `tools/call` result, or an error result when none came.
    func call(_ tool: String, _ arguments: [String: String]) -> [String: Any] {
        guard let answer = request("tools/call", ["name": tool, "arguments": arguments]) else {
            return ["content": [["type": "text", "text": "\(name) closed without answering."]], "isError": true]
        }
        if let result = answer["result"] as? [String: Any] { return result }
        let message = (answer["error"] as? [String: Any])?["message"] as? String ?? "\(name) answered with an error."
        return ["content": [["type": "text", "text": message]], "isError": true]
    }

    func close() {
        try? input.fileHandleForWriting.close()
        process.waitUntilExit()
    }

    private func send(_ object: [String: Any]) {
        guard let data = try? JSONSerialization.data(withJSONObject: object) else { return }
        input.fileHandleForWriting.write(data + Data([10]))
    }

    private func request(_ method: String, _ params: [String: Any]) -> [String: Any]? {
        let id = nextId; nextId += 1
        send(["jsonrpc": "2.0", "id": id, "method": method, "params": params])
        while let line = readLine() {
            guard let object = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any] else { continue }
            if object["id"] as? Int == id { return object }
        }
        return nil
    }

    private func readLine() -> Data? {
        while true {
            if let newline = buffer.firstIndex(of: 10) {
                let line = Data(buffer[buffer.startIndex ..< newline]); buffer.removeSubrange(buffer.startIndex ... newline)
                return line
            }
            let chunk = output.fileHandleForReading.availableData
            if chunk.isEmpty { return nil }
            buffer.append(chunk)
        }
    }
}

final class FakeClaude {
    private let arguments: [String]
    private let environment: [String: String]
    private let script: Script
    private let streamJSON: Bool
    private let sessionId: String
    private let model: String
    private let cwd = FileManager.default.currentDirectoryPath
    private let inbox = Inbox()
    private let outputLock = NSLock()
    private var servers: [String: MCPConnection] = [:]
    private var configured: [String] = []
    private var lastUuid: String?
    private var counter = 0
    private var started = false

    init(arguments: [String], environment: [String: String]) {
        self.arguments = arguments; self.environment = environment
        let data = environment["FAKE_CLAUDE_SCRIPT"].flatMap { FileManager.default.contents(atPath: $0) }
        script = data.flatMap { try? JSONDecoder().decode(Script.self, from: $0) } ?? Script(turns: [])
        streamJSON = Self.value(of: "--input-format", in: arguments) == "stream-json"
        sessionId = Self.value(of: "--resume", in: arguments) ?? script.sessionId ?? UUID().uuidString.lowercased()
        model = script.model ?? "claude-fake-1"
    }

    static func value(of option: String, in arguments: [String]) -> String? {
        guard let index = arguments.firstIndex(of: option), index + 1 < arguments.count else { return nil }
        return arguments[index + 1]
    }

    func run() -> Never {
        log(["event": "argv", "arguments": arguments])
        connectServers()
        inbox.start(streamJSON: streamJSON)
        var turn = 0
        while let message = inbox.next() {
            switch message["type"] as? String {
            case "control_request":
                let request = message["request"] as? [String: Any]
                guard let id = message["request_id"] as? String else { continue }
                if request?["subtype"] as? String == "initialize" {
                    emit(["type": "control_response", "response": ["subtype": "success", "request_id": id,
                          "response": ["commands": [], "models": [], "output_style": "default"] as [String: Any]] as [String: Any]])
                    log(["event": "initialize"])
                } else {
                    emit(["type": "control_response", "response": ["subtype": "error", "request_id": id, "error": "Unsupported control request."]])
                }
            case "user":
                let steps = turn < script.turns.count ? script.turns[turn] : []
                turn += 1
                play(steps, prompt: (message["message"] as? [String: Any])?["content"] ?? "")
            default: continue
            }
        }
        finish(0)
    }

    // MARK: Servers

    private func connectServers() {
        guard let value = Self.value(of: "--mcp-config", in: arguments) else { return }
        let data = value.hasPrefix("{") ? Data(value.utf8) : FileManager.default.contents(atPath: value) ?? Data()
        guard let config = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let entries = config["mcpServers"] as? [String: [String: Any]] else { return }
        configured = entries.keys.sorted()
        // The delegation server is the one these scripts call.
        guard let entry = entries[DelegationMCPServer.serverName], let command = entry["command"] as? String else { return }
        do {
            let connection = try MCPConnection(name: DelegationMCPServer.serverName, command: command, arguments: entry["args"] as? [String] ?? [])
            servers[connection.name] = connection
            log(["event": "mcp", "server": connection.name, "tools": connection.tools])
        } catch {
            log(["event": "mcp", "server": DelegationMCPServer.serverName, "error": error.localizedDescription])
        }
    }

    // MARK: A turn

    private func play(_ steps: [Step], prompt: Any) {
        if !started {
            started = true
            let tools = ["Bash", "Edit", "Read", "Write"] + servers.values.flatMap { server in server.tools.map { "mcp__\(server.name)__\($0)" } }
            emit(["type": "system", "subtype": "init", "session_id": sessionId, "cwd": cwd, "model": model, "tools": tools,
                  "mcp_servers": configured.map { ["name": $0, "status": servers[$0] == nil ? "pending" : "connected"] },
                  "permissionMode": Self.value(of: "--permission-mode", in: arguments) ?? "default", "uuid": UUID().uuidString.lowercased()])
        }
        stateChanged("running")
        record(["type": "user", "message": ["role": "user", "content": prompt]])
        var texts: [String] = []
        for step in steps {
            if let text = step.say {
                texts.append(text)
                assistant([["type": "text", "text": text]])
            } else if let tool = step.call {
                let server = step.server ?? DelegationMCPServer.serverName
                let input = step.arguments ?? [:]
                let id = nextToolUseId()
                assistant([["type": "tool_use", "id": id, "name": "mcp__\(server)__\(tool)", "input": input]])
                if step.ask == true, let denial = ask("mcp__\(server)__\(tool)", input: input, toolUseId: id) {
                    toolResult(id, content: [["type": "text", "text": denial]], isError: true); continue
                }
                guard let connection = servers[server] else {
                    toolResult(id, content: [["type": "text", "text": "No MCP server named \(server)."]], isError: true); continue
                }
                let result = connection.call(tool, input)
                log(["event": "call", "server": server, "tool": tool, "arguments": input, "result": result])
                toolResult(id, content: result["content"] ?? [], isError: result["isError"] as? Bool == true)
            } else if let tool = step.permission {
                let input = step.input ?? [:]
                let id = nextToolUseId()
                assistant([["type": "tool_use", "id": id, "name": tool, "input": input]])
                if let denial = ask(tool, input: input, toolUseId: id) {
                    toolResult(id, content: [["type": "text", "text": denial]], isError: true)
                } else {
                    toolResult(id, content: [["type": "text", "text": "\(tool) ran."]], isError: false)
                }
            } else if let path = step.write {
                let url = URL(fileURLWithPath: path, relativeTo: URL(fileURLWithPath: cwd, isDirectory: true))
                try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
                try? Data((step.text ?? "").utf8).write(to: url)
                log(["event": "write", "path": path])
            } else if let seconds = step.sleep {
                Thread.sleep(forTimeInterval: seconds)
            } else if let code = step.exit {
                finish(code)
            }
        }
        emit(["type": "result", "subtype": "success", "is_error": false, "duration_ms": 1, "num_turns": 1,
              "result": texts.last ?? "", "session_id": sessionId, "total_cost_usd": 0,
              "usage": ["input_tokens": 1, "output_tokens": 1], "uuid": UUID().uuidString.lowercased()])
        stateChanged("idle")
    }

    /// Asks the host whether `tool` may run, the way the CLI does under
    /// `--permission-prompt-tool stdio`. Nil when allowed, else why not.
    private func ask(_ tool: String, input: [String: String], toolUseId: String) -> String? {
        guard streamJSON, Self.value(of: "--permission-prompt-tool", in: arguments) == "stdio" else {
            log(["event": "permission", "tool": tool, "behavior": "deny", "reason": "no permission prompt tool"])
            return "Permission to use \(tool) was not granted."
        }
        let requestId = "perm-\(nextNumber())"
        emit(["type": "control_request", "request_id": requestId,
              "request": ["subtype": "can_use_tool", "tool_name": tool, "input": input, "tool_use_id": toolUseId, "permission_suggestions": []] as [String: Any]])
        guard let answer = inbox.next({ message in
            message["type"] as? String == "control_response" && (message["response"] as? [String: Any])?["request_id"] as? String == requestId
        }), let response = answer["response"] as? [String: Any] else {
            log(["event": "permission", "tool": tool, "behavior": "unanswered"])
            return "The permission request was not answered."
        }
        let decision = response["response"] as? [String: Any] ?? [:]
        let behavior = response["subtype"] as? String == "success" ? decision["behavior"] as? String ?? "deny" : "error"
        var entry: [String: Any] = ["event": "permission", "tool": tool, "behavior": behavior, "toolUseID": decision["toolUseID"] ?? NSNull()]
        if let updated = decision["updatedInput"] { entry["updatedInput"] = updated }
        log(entry)
        return behavior == "allow" ? nil : decision["message"] as? String ?? "Permission to use \(tool) was denied."
    }

    // MARK: Output

    private func assistant(_ content: [[String: Any]]) {
        let message: [String: Any] = ["id": "msg_fake_\(nextNumber())", "type": "message", "role": "assistant", "model": model, "content": content,
                                      "stop_reason": NSNull(), "stop_sequence": NSNull(), "usage": ["input_tokens": 1, "output_tokens": 1]]
        let uuid = record(["type": "assistant", "message": message, "requestId": "req_fake_\(counter)"])
        emit(["type": "assistant", "message": message, "parent_tool_use_id": NSNull(), "session_id": sessionId, "uuid": uuid])
    }

    private func toolResult(_ id: String, content: Any, isError: Bool) {
        let block: [String: Any] = ["type": "tool_result", "tool_use_id": id, "content": content, "is_error": isError]
        let message: [String: Any] = ["role": "user", "content": [block]]
        let uuid = record(["type": "user", "message": message, "toolUseResult": content])
        emit(["type": "user", "message": message, "parent_tool_use_id": NSNull(), "session_id": sessionId, "uuid": uuid])
    }

    private func stateChanged(_ state: String) {
        guard environment["CLAUDE_CODE_EMIT_SESSION_STATE_EVENTS"] == "1" else { return }
        emit(["type": "system", "subtype": "session_state_changed", "state": state, "session_id": sessionId])
    }

    private func emit(_ object: [String: Any]) {
        guard let data = try? JSONSerialization.data(withJSONObject: object, options: [.withoutEscapingSlashes]) else { return }
        outputLock.withLock { FileHandle.standardOutput.write(data + Data([10])) }
    }

    /// Appends one line to the session's transcript, linked to the line
    /// before it as Claude Code links them, and returns its uuid.
    @discardableResult private func record(_ fields: [String: Any]) -> String {
        let uuid = UUID().uuidString.lowercased()
        var line: [String: Any] = ["parentUuid": lastUuid ?? NSNull(), "isSidechain": false, "userType": "external", "cwd": cwd,
                                   "sessionId": sessionId, "version": "2.1.271", "gitBranch": "", "uuid": uuid,
                                   "timestamp": ISO8601DateFormatter().string(from: Date())]
        line.merge(fields) { _, new in new }
        lastUuid = uuid
        let configDirectory = environment["CLAUDE_CONFIG_DIR"].flatMap { $0.isEmpty ? nil : $0 }
            ?? (environment["HOME"] ?? NSHomeDirectory()) + "/.claude"
        let folder = String(cwd.unicodeScalars.map { $0.isASCII && CharacterSet.alphanumerics.contains($0) ? Character($0) : "-" })
        let directory = URL(fileURLWithPath: configDirectory, isDirectory: true).appendingPathComponent("projects/\(folder)", isDirectory: true)
        append(line, to: directory.appendingPathComponent("\(sessionId).jsonl"))
        return uuid
    }

    private func log(_ object: [String: Any]) {
        guard let path = environment["FAKE_CLAUDE_LOG"], !path.isEmpty else { return }
        append(object, to: URL(fileURLWithPath: path))
    }

    private func append(_ object: [String: Any], to url: URL) {
        guard let data = try? JSONSerialization.data(withJSONObject: object, options: [.sortedKeys, .withoutEscapingSlashes]) else { return }
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        if !FileManager.default.fileExists(atPath: url.path) { FileManager.default.createFile(atPath: url.path, contents: nil) }
        guard let handle = try? FileHandle(forWritingTo: url) else { return }
        defer { try? handle.close() }
        _ = try? handle.seekToEnd()
        try? handle.write(contentsOf: data + Data([10]))
    }

    private func nextNumber() -> Int { counter += 1; return counter }
    private func nextToolUseId() -> String { "toolu_fake_\(nextNumber())" }

    private func finish(_ code: Int32) -> Never {
        servers.values.forEach { $0.close() }
        log(["event": "exit", "code": Int(code)])
        exit(code)
    }
}
