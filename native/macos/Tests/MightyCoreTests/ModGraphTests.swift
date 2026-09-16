import Foundation
import Testing
@testable import MightyCore

private final class ModGraphRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var entries: [ModMetadata] = []
    func append(_ entry: ModMetadata) { lock.lock(); entries.append(entry); lock.unlock() }
    func values() -> [ModMetadata] { lock.lock(); defer { lock.unlock() }; return entries }
}

struct ModGraphTests {
    private func body(_ environment: [String: String], event: String = "agent.spawn", graph: [String: Any]) -> [String: Any] {
        ["version": 1, "runId": environment["MIGHTY_CLAUDE_RUN_ID"]!, "claudeSessionId": "graph-session", "event": event, "sequence": 1, "graph": graph]
    }
    private func post(_ environment: [String: String], _ payload: [String: Any], graph: Bool = true, headers: [String: String] = [:]) async throws -> Int {
        var request = URLRequest(url: URL(string: environment["MIGHTY_CLAUDE_BRIDGE_URL"]!)!, timeoutInterval: 3)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("Bearer " + environment["MIGHTY_CLAUDE_BRIDGE_TOKEN"]!, forHTTPHeaderField: "Authorization")
        if graph { request.setValue("1", forHTTPHeaderField: "X-Mighty-Graph") }
        for (name, value) in headers { request.setValue(value, forHTTPHeaderField: name) }
        request.httpBody = try JSONSerialization.data(withJSONObject: payload)
        let (_, response) = try await URLSession.shared.data(for: request)
        return (response as? HTTPURLResponse)?.statusCode ?? 0
    }

    @Test func graphOptInCarriesBoundedAgentContentAndPreservesLegacyLimits() async throws {
        let recorder = ModGraphRecorder(), bridge = try ModBridge(graphEnabled: true) { recorder.append($0) }
        let environment = try await bridge.start()
        do {
            #expect(environment["MIGHTY_CLAUDE_GRAPH"] == "1")
            let starting: [String: Any] = ["version": 1, "phase": "starting", "parentAgentId": "parent-agent", "parentToolUseId": "agent-tool", "name": "Reader", "agentType": "Explore", "input": String(repeating: "x", count: 16_384)]
            #expect(try await post(environment, body(environment, graph: starting)) == 200)
            let finished: [String: Any] = ["version": 1, "phase": "completed", "agentId": "child-agent", "parentAgentId": "parent-agent", "parentToolUseId": "agent-tool", "model": "claude-sonnet-4-6", "output": String(repeating: "r", count: 32_768)]
            let large = body(environment, event: "agent.complete", graph: finished)
            #expect(try await post(environment, large) == 200)
            #expect(recorder.values().last?.graph?.output?.utf8.count == 32_768)
            #expect(recorder.values().last?.graph?.parentToolUseId == "agent-tool")
            #expect(try await post(environment, large, graph: false) == 413)
            let small = body(environment, graph: ["version": 1, "phase": "starting", "parentToolUseId": "small"])
            #expect(try await post(environment, small, graph: false) == 400)
            let ordinary: [String: Any] = ["version": 1, "runId": environment["MIGHTY_CLAUDE_RUN_ID"]!, "claudeSessionId": "graph-session", "event": "tool.complete", "tool": "Read", "toolUseId": "read", "output": String(repeating: "o", count: 20_000)]
            #expect(try await post(environment, ordinary) == 413)
            #expect(recorder.values().count == 2)
        } catch { await bridge.stop(); throw error }
        await bridge.stop()
    }

    @Test func graphRemainsAuthenticatedRunScopedAndDisabledByDefault() async throws {
        let recorder = ModGraphRecorder(), bridge = try ModBridge { recorder.append($0) }
        let environment = try await bridge.start()
        do {
            #expect(environment["MIGHTY_CLAUDE_GRAPH"] == "0")
            let graph = body(environment, graph: ["version": 1, "phase": "running", "agentId": "child", "parentToolUseId": "spawn"])
            #expect(try await post(environment, graph) == 400)
            var legacy = graph; legacy.removeValue(forKey: "graph"); legacy["event"] = "session.start"
            #expect(try await post(environment, legacy, graph: false) == 200)
            #expect(try await post(environment, graph, headers: ["Authorization": "Bearer wrong"]) == 401)
            #expect(try await post(environment, graph, headers: ["Origin": "https://example.invalid"]) == 403)
            var foreign = legacy; foreign["runId"] = "other-run"
            #expect(try await post(environment, foreign) == 400)
            #expect(recorder.values().count == 1 && recorder.values().first?.event == "session.start")
        } catch { await bridge.stop(); throw error }
        await bridge.stop()
    }

    @Test func malformedGraphCannotForgeIdentitiesOrExceedContentBounds() async throws {
        let recorder = ModGraphRecorder(), bridge = try ModBridge(graphEnabled: true) { recorder.append($0) }
        let environment = try await bridge.start()
        let valid: [String: Any] = ["version": 1, "phase": "running", "parentToolUseId": "spawn", "agentId": "child"]
        do {
            let changes: [[String: Any]] = [
                ["version": true], ["version": 2], ["phase": "completed"], ["agentId": "child\n"],
                ["parentAgentId": "child"], ["agentId": 123], ["model": "not a model"],
                ["input": String(repeating: "i", count: 16_385)], ["output": String(repeating: "o", count: 32_769)],
                ["credentials": "not accepted"], ["name": "\u{1b}[31mforged"],
            ]
            for change in changes {
                var graph = valid; graph.merge(change) { _, new in new }
                #expect(try await post(environment, body(environment, graph: graph)) == 400)
            }
            var noParent = valid; noParent.removeValue(forKey: "parentToolUseId")
            #expect(try await post(environment, body(environment, graph: noParent)) == 400)
            #expect(try await post(environment, body(environment, event: "agent.complete", graph: ["version": 1, "phase": "completed"])) == 400)
            #expect(recorder.values().isEmpty)
        } catch { await bridge.stop(); throw error }
        await bridge.stop()
    }
}
