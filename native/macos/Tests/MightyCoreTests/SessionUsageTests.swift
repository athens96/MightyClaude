import Foundation
import Testing
@testable import MightyCore

private final class UsageRecorder<T: Sendable>: @unchecked Sendable {
    private let lock = NSLock(); private var entries: [T] = []
    func append(_ value: T) { lock.lock(); entries.append(value); lock.unlock() }
    func values() -> [T] { lock.lock(); defer { lock.unlock() }; return entries }
}

struct SessionUsageTests {
    private func push(_ value: [String: Any], into parser: CLIStreamParser) throws {
        parser.push(try JSONSerialization.data(withJSONObject: value) + Data([10]))
    }
    private func directory() throws -> URL {
        let value = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-usage-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: value, withIntermediateDirectories: true); return value
    }
    private func wait(_ condition: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(5)
        while !condition() {
            guard Date() < deadline else { throw MightyError("Usage fixture timed out") }
            try await Task.sleep(for: .milliseconds(10))
        }
    }
    private func gemini(_ directory: URL) throws -> URL {
        let file = directory.appendingPathComponent("gemini")
        let source = #"""
        #!/bin/sh
        if [ "$1" = '--version' ]; then printf '0.43.0\n'; exit 0; fi
        /bin/cat >/dev/null
        printf '%s\n' '{"type":"init","session_id":"gemini-fixture","model":"gemini-2.5-pro"}'
        printf '%s\n' '{"type":"result","status":"success","stats":{"input_tokens":1200,"output_tokens":80,"total_tokens":1300,"cached":400}}'
        """#
        try Data(source.utf8).write(to: file)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: file.path)
        return file
    }

    @Test func codexTotalsReplaceAcrossResumeAndNeverBecomeContext() throws {
        var samples: [SessionUsage] = []
        let parser = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in }, usage: { samples.append($0) })
        try push(["type": "thread.started", "thread_id": "thread-one"], into: parser)
        let usage: [String: Any] = ["type": "turn.completed", "usage": ["input_tokens": 1000, "cached_input_tokens": 600, "cache_write_input_tokens": 20, "output_tokens": 100, "reasoning_output_tokens": 40]]
        try push(usage, into: parser); let count = samples.count
        try push(usage, into: parser)
        #expect(samples.count == count)
        #expect(samples.last?.totalTokens == 1100 && samples.last?.inputTokens == 1000)
        #expect(samples.last?.tokenScope == "session" && samples.last?.reasoningTokens == 40)
        #expect(samples.last?.contextUsedTokens == nil && samples.last?.contextWindowTokens == nil && samples.last?.contextPercent == nil)
        try push(["type": "turn.completed", "usage": ["input_tokens": 1400, "output_tokens": 120]], into: parser)
        #expect(samples.last?.totalTokens == 1520)
        let valid = samples.last
        try push(["type": "turn.completed", "usage": ["input_tokens": true, "output_tokens": -1]], into: parser)
        #expect(samples.last == valid)
        try push(["type": "thread.started", "thread_id": "thread-two"], into: parser)
        #expect(samples.last?.providerSessionId == "thread-two" && samples.last?.totalTokens == nil)
        var resumed: [SessionUsage] = []
        let next = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in }, usage: { resumed.append($0) })
        try push(usage, into: next)
        #expect(resumed.last?.totalTokens == 1100)
    }

    @Test func geminiCumulativePromptIncludesCacheAndKeepsExactContextUnknown() throws {
        var samples: [SessionUsage] = []
        let parser = CLIStreamParser(provider: "gemini", log: { _, _ in }, resume: { _ in }, usage: { samples.append($0) })
        try push(["type": "init", "session_id": "gemini-one", "model": "auto"], into: parser)
        let result: [String: Any] = ["type": "result", "stats": ["input_tokens": 1000, "input": 400, "cached": 600, "output_tokens": 100, "total_tokens": 1150, "models": ["gemini-2.5-pro": [:]]]]
        try push(result, into: parser); let count = samples.count; try push(result, into: parser)
        #expect(samples.count == count)
        #expect(samples.last?.inputTokens == 1000 && samples.last?.cacheReadTokens == 600 && samples.last?.totalTokens == 1150)
        #expect(samples.last?.model == "gemini-2.5-pro" && samples.last?.tokenScope == "session")
        #expect(samples.last?.contextPercent == nil && samples.last?.reasoningTokens == nil)
        let last = samples.last
        try push(["type": "result", "stats": ["input_tokens": -1, "output_tokens": 0, "total_tokens": 0]], into: parser)
        #expect(samples.last == last)
    }

    @Test func claudeCountsCacheOnceAndSeparatesResponseContextFromRunTotals() throws {
        var samples: [SessionUsage] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, usage: { samples.append($0) })
        let response: [String: Any] = ["type": "assistant", "session_id": "claude-one", "uuid": "block-one", "message": ["id": "api-response", "model": "claude-sonnet-4-6", "content": [], "usage": ["input_tokens": 100, "cache_read_input_tokens": 500, "cache_creation_input_tokens": 200, "output_tokens": 20]]]
        try push(response, into: parser); let count = samples.count; try push(response, into: parser)
        #expect(samples.count == count && samples.last?.inputTokens == 800 && samples.last?.totalTokens == 820)
        #expect(samples.last?.contextUsedTokens == 800 && samples.last?.contextPercent == nil)
        var subagent = response; subagent["parent_tool_use_id"] = "subagent-call"
        try push(subagent, into: parser); #expect(samples.count == count)
        try push(["type": "system", "subtype": "compact_boundary", "session_id": "claude-one"], into: parser)
        #expect(samples.last?.contextUsedTokens == nil)
        try push(response, into: parser)
        let row: [String: Any] = ["inputTokens": 300, "outputTokens": 60, "cacheReadInputTokens": 1500, "cacheCreationInputTokens": 400, "thinkingTokens": 10, "contextWindow": 200_000]
        let result: [String: Any] = ["type": "result", "session_id": "claude-one", "total_cost_usd": 0.25, "modelUsage": ["claude-sonnet-4-6": row]]
        try push(result, into: parser); let finalCount = samples.count; try push(result, into: parser)
        #expect(samples.count == finalCount)
        #expect(samples.last?.tokenScope == "run" && samples.last?.inputTokens == 2200 && samples.last?.totalTokens == 2260)
        #expect(samples.last?.contextUsedTokens == 800 && samples.last?.contextWindowTokens == 200_000)
        #expect(samples.last?.contextPercent == 0.4 && samples.last?.costScope == "run")
    }

    @Test func modsContextAndQuotaUseActualReadingsAndIndependentFreshness() throws {
        var samples: [SessionUsage] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, usage: { samples.append($0) })
        let stamp = "2026-09-16T10:00:00Z"
        let initial = SessionUsage(provider: "claude", source: "claude.mods", tokenScope: "session", model: "claude-sonnet-4-6", providerSessionId: "claude-one", contextWindowTokens: 200_000, costUSD: 2, costScope: "session", rateLimits: [.init(kind: "five_hour", percentUsed: 30, resetsAt: "2026-09-16T12:00:00Z"), .init(kind: "spend_limit", percentUsed: 120)], updatedAt: stamp)
        parser.receiveMod(ModMetadata(claudeSessionId: "claude-one", event: "session.usage", sequence: 1, usage: initial))
        #expect(samples.last?.contextPercent == nil && samples.last?.rateLimitsUpdatedAt == stamp)
        try push(["type": "assistant", "session_id": "claude-one", "message": ["model": "claude-sonnet-4-6", "content": [], "usage": ["input_tokens": 1000, "output_tokens": 20]]], into: parser)
        #expect(samples.last?.contextPercent == 0.5)
        #expect(samples.last?.rateLimitsUpdatedAt == stamp && samples.last?.updatedAt != stamp)
        var current = initial; current.contextUsedTokens = 40_000; current.costUSD = 2.5
        parser.receiveMod(ModMetadata(claudeSessionId: "claude-one", event: "session.usage", sequence: 3, usage: current))
        let observed = samples.last
        parser.receiveMod(ModMetadata(claudeSessionId: "claude-one", event: "session.usage", sequence: 2, usage: initial))
        #expect(samples.last == observed && samples.last?.contextPercent == 20)
        var compacted = current; compacted.contextUsedTokens = nil
        parser.receiveMod(ModMetadata(claudeSessionId: "claude-one", event: "session.usage", sequence: 4, usage: compacted))
        #expect(samples.last?.contextPercent == nil && samples.last?.costScope == "session")
    }

    @Test func optionalDamageAndOtherProvidersCannotEraseSavedConversation() async throws {
        let root = try directory(); defer { try? FileManager.default.removeItem(at: root) }
        let repository = StateRepository(directory: root, legacyStateURL: nil)
        let workspace = try await repository.approveWorkspace(Workspace(name: "Usage", path: root.path))
        let usage = SessionUsage(provider: "codex", source: "codex.exec", tokenScope: "session", inputTokens: 100, outputTokens: 20, totalTokens: 120)
        var session = RunSession(id: "pane", workspaceId: workspace.id, title: "Preserved", provider: "codex", logs: [.init(kind: "assistant", text: "Keep this")], sessionUsage: usage)
        session.recordSessionUsage(RunEvent(sessionId: "other", type: "usage", usage: usage))
        var other = usage; other.provider = "claude"
        session.recordSessionUsage(RunEvent(sessionId: "pane", type: "usage", usage: other))
        #expect(session.sessionUsage == usage)
        var invalid = usage; invalid.source = "\u{1b}invalid"
        session.recordSessionUsage(RunEvent(sessionId: "pane", type: "usage", usage: invalid))
        #expect(session.sessionUsage == usage)
        try await repository.save(AppSnapshot(workspaces: [workspace], sessions: [session]))
        let restored = try await StateRepository(directory: root, legacyStateURL: nil).load()
        #expect(restored.sessions.first?.sessionUsage == usage)
        var object = try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(session)) as? [String: Any])
        object["sessionUsage"] = ["provider": false]
        let decoded = try JSONDecoder().decode(RunSession.self, from: JSONSerialization.data(withJSONObject: object))
        #expect(decoded.title == "Preserved" && decoded.logs.first?.text == "Keep this" && decoded.sessionUsage == nil)
        var damaged = usage; damaged.inputTokens = -10; damaged.contextWindowTokens = 0; damaged.costUSD = .infinity
        let clean = try #require(SessionUsageSupport.normalized(damaged))
        #expect(clean.inputTokens == nil && clean.contextPercent == nil && clean.costUSD == nil)
        let badWire = Data(#"{"sessionId":"pane","type":"usage","usage":{"provider":false}}"#.utf8)
        #expect(try JSONDecoder().decode(RunEvent.self, from: badWire).usage == nil)
    }

    @Test func authenticatedModUsageCrossesRealLoopbackWithoutPrompts() async throws {
        let received = UsageRecorder<ModMetadata>()
        let bridge = try ModBridge { received.append($0) }
        let environment = try await bridge.start()
        do {
            #expect(environment["MIGHTY_CLAUDE_USAGE"] == "1")
            let usage = SessionUsage(provider: "claude", source: "claude.mods", tokenScope: "session", providerSessionId: "claude-one", contextUsedTokens: 50_000, contextWindowTokens: 200_000, rateLimits: [.init(kind: "seven_day", percentUsed: 40)])
            let payload: [String: Any] = ["version": 1, "runId": environment["MIGHTY_CLAUDE_RUN_ID"]!, "claudeSessionId": "claude-one", "event": "session.usage", "sequence": 1, "usage": try JSONSerialization.jsonObject(with: JSONEncoder().encode(usage))]
            var request = URLRequest(url: URL(string: environment["MIGHTY_CLAUDE_BRIDGE_URL"]!)!)
            request.httpMethod = "POST"; request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.setValue("Bearer " + environment["MIGHTY_CLAUDE_BRIDGE_TOKEN"]!, forHTTPHeaderField: "Authorization")
            request.httpBody = try JSONSerialization.data(withJSONObject: payload)
            let (_, response) = try await URLSession.shared.data(for: request)
            #expect((response as? HTTPURLResponse)?.statusCode == 200)
            #expect(received.values().first?.usage?.contextPercent == 25)
            request.setValue("Bearer invalid", forHTTPHeaderField: "Authorization")
            let (_, denied) = try await URLSession.shared.data(for: request)
            #expect((denied as? HTTPURLResponse)?.statusCode == 401 && received.values().count == 1)
        } catch { await bridge.stop(); throw error }
        await bridge.stop()
    }

    @Test func actualRunnerAndRemoteClientCarryUsageFromFakeGemini() async throws {
        let root = try directory(); defer { try? FileManager.default.removeItem(at: root) }
        let binary = try gemini(root), unavailable = URL(fileURLWithPath: "/usr/bin/false")
        let providers = ProviderService(binaryOverrides: ["claude": unavailable, "codex": unavailable, "gemini": binary])
        let repository = StateRepository(directory: root.appendingPathComponent("host-state"), legacyStateURL: nil)
        let workspace = try await repository.approveWorkspace(Workspace(name: "Remote usage", path: root.path))
        try await repository.save(AppSnapshot(workspaces: [workspace]))
        let received = UsageRecorder<RunEvent>()
        let host = RemoteService(repository: repository, providers: providers, pluginDirectory: root, dataDirectory: root.appendingPathComponent("host-remote"), onEvent: { _ in }, allowLoopbackForTests: true)
        let client = RemoteService(repository: StateRepository(directory: root.appendingPathComponent("client-state"), legacyStateURL: nil), providers: providers, pluginDirectory: root, dataDirectory: root.appendingPathComponent("client-remote"), onEvent: { received.append($0) }, allowLoopbackForTests: true)
        do {
            let shared = try await host.startSharing(workspaceIds: [workspace.id], port: 0)
            let state = try await client.connectRemote(name: "Usage fixture", address: try #require(shared.host.address), token: try #require(shared.host.token))
            let connection = try #require(state.connections.first)
            let imported = try await client.importWorkspace(connectionId: connection.id, workspaceId: workspace.id)
            try await client.start(request: StartRunRequest(sessionId: "remote-pane", workspaceId: imported.id, input: "fixture only", provider: "gemini"), workspace: imported)
            try await wait { received.values().contains { $0.status == "completed" } }
            let usage = try #require(received.values().last { $0.type == "usage" }?.usage)
            #expect(usage.inputTokens == 1200 && usage.totalTokens == 1300 && usage.contextPercent == nil)
            #expect(usage.providerSessionId == "gemini-fixture" && usage.model == "gemini-2.5-pro")
            #expect(received.values().filter { $0.type == "usage" }.allSatisfy { $0.sessionId == "remote-pane" })
        } catch { await client.shutdown(); await host.shutdown(); await providers.shutdown(); throw error }
        await client.shutdown(); await host.shutdown(); await providers.shutdown()
    }
}
