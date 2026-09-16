import Foundation
import Security

/// Opt-in observations from the engine's agent.spawn / turn.complete hooks.
/// IDs describe actual agent loops; input/output are bounded visible text only.
public struct ModGraphMetadata: Codable, Sendable, Equatable {
    public let version: Int
    public let phase: String
    public let agentId: String?
    public let parentAgentId: String?
    public let parentToolUseId: String?
    public let name: String?
    public let agentType: String?
    public let model: String?
    public let input: String?
    public let output: String?
    public init(version: Int = 1, phase: String, agentId: String? = nil, parentAgentId: String? = nil, parentToolUseId: String? = nil, name: String? = nil, agentType: String? = nil, model: String? = nil, input: String? = nil, output: String? = nil) {
        self.version = version; self.phase = phase; self.agentId = agentId; self.parentAgentId = parentAgentId
        self.parentToolUseId = parentToolUseId; self.name = name; self.agentType = agentType
        self.model = model; self.input = input; self.output = output
    }
}

public struct ModMetadata: Sendable, Equatable {
    public let claudeSessionId: String
    public let event: String
    public let tool: String?
    public let reason: String?
    public let toolUseId: String?
    public let summary: String?
    public let output: String?
    public let isError: Bool?
    public let sequence: Int?
    public let agentId: String?
    public let usage: SessionUsage?
    public let graph: ModGraphMetadata?
    public init(claudeSessionId: String, event: String, tool: String? = nil, reason: String? = nil, toolUseId: String? = nil, summary: String? = nil, output: String? = nil, isError: Bool? = nil, sequence: Int? = nil, agentId: String? = nil, usage: SessionUsage? = nil, graph: ModGraphMetadata? = nil) {
        self.claudeSessionId = claudeSessionId; self.event = event; self.tool = tool; self.reason = reason
        self.toolUseId = toolUseId; self.summary = summary; self.output = output; self.isError = isError; self.sequence = sequence; self.agentId = agentId; self.usage = usage; self.graph = graph
    }
}

public actor ModBridge {
    private let onEvent: @Sendable (ModMetadata) -> Void
    private let runId = UUID().uuidString
    private let token: String
    private let graphEnabled: Bool
    private var server: HTTPServer?
    private var closed = false
    private var windowStarted = Date()
    private var requestCount = 0
    public private(set) var receivedCount = 0
    public init(graphEnabled: Bool = false, onEvent: @escaping @Sendable (ModMetadata) -> Void) throws {
        var bytes = [UInt8](repeating: 0, count: 32)
        guard SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes) == errSecSuccess else { throw MightyError("안전한 Mods 연결 키를 만들지 못했습니다.") }
        token = bytes.map { String(format: "%02x", $0) }.joined(); self.onEvent = onEvent; self.graphEnabled = graphEnabled
    }
    public func start() async throws -> [String: String] {
        guard !closed, server == nil else { throw MightyError("Mods 연결이 이미 시작되었거나 종료되었습니다.") }
        let expectedAuthorization = Array(("Bearer " + token).utf8), acceptsGraph = graphEnabled
        let http = HTTPServer(address: "127.0.0.1", port: 0, requestBodyLimit: { request in
            // The larger body allowance is limited to authenticated graph
            // uploads from this run, before HTTPServer buffers their bodies.
            guard acceptsGraph, request.headers["x-mighty-graph"] == "1", request.method == "POST", request.target == "/events",
                  request.remoteAddress == "127.0.0.1", request.headers["origin"] == nil, request.headers["sec-fetch-site"] == nil,
                  modAuthorizationMatches(request.headers["authorization"], expected: expectedAuthorization) else { return 16_384 }
            return 65_536
        }) { [weak self] request in
            guard let self else { return .json(503, ["error": "closed"]) }
            return await self.receive(request)
        }
        server = http
        do {
            let port = try await http.start()
            guard !closed else { await http.stop(); throw MightyError("Mods 연결이 취소되었습니다.") }
            return ["CLAUDE_CODE_ENABLE_FUNCTION_HOOKS": "1", "MIGHTY_CLAUDE_BRIDGE_URL": "http://127.0.0.1:\(port)/events", "MIGHTY_CLAUDE_BRIDGE_TOKEN": token, "MIGHTY_CLAUDE_RUN_ID": runId, "MIGHTY_CLAUDE_ACTIVITY": "1", "MIGHTY_CLAUDE_USAGE": "1", "MIGHTY_CLAUDE_GRAPH": graphEnabled ? "1" : "0"]
        } catch { await http.stop(); server = nil; throw error }
    }
    private func receive(_ request: HTTPRequest) -> HTTPResponse {
        guard !closed else { return .json(503, ["error": "closed"]) }
        guard request.method == "POST", request.target == "/events", request.headers["origin"] == nil, request.headers["sec-fetch-site"] == nil, request.remoteAddress == "127.0.0.1" else { return .json(403, ["error": "forbidden"]) }
        let graphRequest = graphEnabled && request.headers["x-mighty-graph"] == "1"
        guard request.body.count <= (graphRequest ? 65_536 : 16_384), request.headers["content-type"]?.split(separator: ";").first?.trimmingCharacters(in: .whitespaces) == "application/json" else { return .json(413, ["error": "invalid payload"]) }
        guard modAuthorizationMatches(request.headers["authorization"], expected: Array(("Bearer " + token).utf8)) else { return .json(401, ["error": "unauthorized"]) }
        if Date().timeIntervalSince(windowStarted) >= 1 { windowStarted = Date(); requestCount = 0 }
        requestCount += 1
        guard requestCount <= 120 else { return .json(429, ["error": "rate limited"]) }
        guard let body = try? JSONSerialization.jsonObject(with: request.body) as? [String: Any], Set(body.keys).isSubset(of: ["version", "runId", "claudeSessionId", "event", "turnId", "tool", "durationMs", "reason", "toolUseId", "summary", "output", "isError", "sequence", "agentId", "usage", "graph"]), body["version"] as? Int == 1, body["runId"] as? String == runId, let session = body["claudeSessionId"] as? String, CoreValidation.identifier(session), let event = body["event"] as? String, ["session.start", "turn.start", "turn.complete", "tool.call", "tool.waiting", "tool.complete", "session.usage", "agent.spawn", "agent.complete"].contains(event) else { return .json(400, ["error": "invalid event"]) }
        if request.body.count > 16_384, !["agent.spawn", "agent.complete"].contains(event) { return .json(413, ["error": "invalid payload"]) }
        if let turn = body["turnId"], !(turn is String) || !CoreValidation.identifier(turn as? String ?? "") { return .json(400, ["error": "invalid turn"]) }
        for (key, limit) in [("tool", 160), ("summary", ActivitySupport.maximumSummaryBytes), ("output", ActivitySupport.maximumOutputBytes)] {
            if let raw = body[key] {
                guard let text = raw as? String, text == ActivitySupport.clean(text, maximumBytes: limit, singleLine: key != "output") else { return .json(400, ["error": "invalid \(key)"]) }
            }
        }
        for key in ["toolUseId", "agentId"] {
            if let value = body[key], !(value is String) || !CoreValidation.identifier(value as? String ?? "") { return .json(400, ["error": "invalid identity"]) }
        }
        if let sequence = body["sequence"] {
            guard let number = sequence as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(), number.doubleValue.isFinite, number.doubleValue.rounded() == number.doubleValue, number.doubleValue >= 0, number.doubleValue <= 9_007_199_254_740_991 else { return .json(400, ["error": "invalid sequence"]) }
        }
        if let flag = body["isError"], CFGetTypeID(flag as CFTypeRef) != CFBooleanGetTypeID() { return .json(400, ["error": "invalid error flag"]) }
        if let duration = body["durationMs"] {
            guard let number = duration as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(), number.doubleValue.isFinite, number.doubleValue >= 0, number.doubleValue <= 1_000_000_000_000 else { return .json(400, ["error": "invalid duration"]) }
        }
        if let reason = body["reason"], !["answer", "aborted", "refusal", "error"].contains(reason as? String ?? "") { return .json(400, ["error": "invalid reason"]) }
        if event.hasPrefix("tool."), (body["tool"] as? String ?? "").isEmpty { return .json(400, ["error": "missing tool"]) }
        if ["tool.waiting", "tool.complete"].contains(event), body["toolUseId"] == nil { return .json(400, ["error": "missing tool identity"]) }
        var usage: SessionUsage?
        if let raw = body["usage"] {
            guard event == "session.usage", let data = try? JSONSerialization.data(withJSONObject: raw),
                  let decoded = try? JSONDecoder().decode(SessionUsage.self, from: data), decoded.provider == "claude",
                  decoded.providerSessionId == session, let normalized = SessionUsageSupport.normalized(decoded) else { return .json(400, ["error": "invalid usage"]) }
            usage = normalized
        }
        if event == "session.usage", usage == nil { return .json(400, ["error": "missing usage"]) }
        var graph: ModGraphMetadata?
        if let raw = body["graph"] {
            guard graphRequest, ["agent.spawn", "agent.complete"].contains(event), let object = raw as? [String: Any],
                  Set(object.keys).isSubset(of: ["version", "phase", "agentId", "parentAgentId", "parentToolUseId", "name", "agentType", "model", "input", "output"]),
                  let version = object["version"] as? NSNumber, CFGetTypeID(version) != CFBooleanGetTypeID(), version.intValue == 1, version.doubleValue == 1,
                  let data = try? JSONSerialization.data(withJSONObject: object), let decoded = try? JSONDecoder().decode(ModGraphMetadata.self, from: data),
                  validModGraph(decoded, event: event) else { return .json(400, ["error": "invalid graph"]) }
            graph = decoded
        }
        if ["agent.spawn", "agent.complete"].contains(event), graph == nil { return .json(400, ["error": "missing graph"]) }
        receivedCount += 1
        onEvent(ModMetadata(claudeSessionId: session, event: event, tool: body["tool"] as? String, reason: body["reason"] as? String, toolUseId: body["toolUseId"] as? String, summary: body["summary"] as? String, output: body["output"] as? String, isError: body["isError"] as? Bool, sequence: body["sequence"] as? Int, agentId: body["agentId"] as? String, usage: usage, graph: graph))
        return .json(200, ["ok": true])
    }
    public func stop() async { closed = true; let old = server; server = nil; await old?.stop() }
}

private func modAuthorizationMatches(_ value: String?, expected: [UInt8]) -> Bool {
    let actual = Array((value ?? "").utf8)
    guard actual.count == expected.count else { return false }
    var difference: UInt8 = 0
    for index in expected.indices { difference |= expected[index] ^ actual[index] }
    return difference == 0
}

private func validModGraph(_ value: ModGraphMetadata, event: String) -> Bool {
    let phases = event == "agent.spawn" ? ["starting", "running", "error", "stopped"] : ["completed", "error", "stopped"]
    guard value.version == 1, phases.contains(value.phase) else { return false }
    for id in [value.agentId, value.parentAgentId, value.parentToolUseId].compactMap({ $0 }) {
        guard CoreValidation.identifier(id), id == ActivitySupport.clean(id, maximumBytes: 128, singleLine: true) else { return false }
    }
    if value.agentId != nil && value.agentId == value.parentAgentId { return false }
    if event == "agent.spawn", value.parentToolUseId == nil { return false }
    if event == "agent.complete" || value.phase == "running", value.agentId == nil { return false }
    for (text, limit, singleLine) in [(value.name, 160, true), (value.agentType, 160, true), (value.model, 200, true), (value.input, 16_384, false), (value.output, 32_768, false)] {
        if let text, text != ActivitySupport.clean(text, maximumBytes: limit, singleLine: singleLine) { return false }
    }
    if let model = value.model, !CoreValidation.model(model) { return false }
    return true
}
