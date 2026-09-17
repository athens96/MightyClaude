import Foundation
import Testing
@testable import MightyCore

/// A scripted app: fixed state, records commands, bumps revisions on demand.
private final class FakeMobileHost: MobileHostDelegate, @unchecked Sendable {
    private let lock = NSLock()
    var stateRevision = 1
    var sessionRevision = 1
    var commands: [String] = []
    var failSubmit = false
    private func summary() -> MobileSessionSummary {
        MobileSessionSummary(id: "session-1", workspaceId: "workspace-1", title: "Claude", kind: "claude", provider: "claude", model: "default", status: "running",
                             revision: sessionRevision, updatedAt: "2026-09-17T00:00:00Z", preview: MobilePreview(kind: "assistant", text: "안녕하세요"), pendingPermissions: 1)
    }
    func mobileState() async -> MobileState {
        lock.lock(); defer { lock.unlock() }
        return MobileState(revision: stateRevision, hostName: "Test Mac", workspaces: [MobileWorkspace(id: "workspace-1", name: "Repo", path: "/tmp/repo", remote: false)], sessions: [summary()])
    }
    func mobileSession(id: String) async -> MobileSessionDetail? {
        lock.lock(); defer { lock.unlock() }
        guard id == "session-1" else { return nil }
        let request = ToolPermissionRequest(id: "perm-1", runId: "run-1", toolUseId: "tool-1", toolName: "Bash", inputJSON: #"{"command":"npm test","description":"Run the test suite"}"#, summary: "npm test", state: "pending", canAllow: true, canAnswerQuestions: false)
        return MobileSessionDetail(revision: sessionRevision, session: summary(), entries: [LogEntry(id: "entry-1", kind: "assistant", text: "안녕하세요")],
                                   permissions: [MobilePermission(request: request)], queued: [MobileQueuedItem(id: "q1", text: "next")], usage: MobileUsage(contextPercent: 12.5), elapsedSeconds: 3)
    }
    func mobileSubmit(sessionId: String, text: String) async throws -> String {
        lock.lock(); defer { lock.unlock() }
        if failSubmit { throw MightyError("실행 준비가 필요합니다.") }
        commands.append("submit:\(sessionId):\(text)"); return "steered"
    }
    func mobileStop(sessionId: String) async throws { lock.lock(); commands.append("stop:\(sessionId)"); lock.unlock() }
    func mobilePermission(sessionId: String, requestId: String, runId: String, allow: Bool) async throws { lock.lock(); commands.append("perm:\(requestId):\(runId):\(allow)"); lock.unlock() }
    func mobileAnswers(sessionId: String, requestId: String, runId: String, answers: [String: UserQuestionAnswer]) async throws {
        lock.lock(); commands.append("answers:\(requestId):\(answers.keys.sorted().joined(separator: ","))"); lock.unlock()
    }
    func mobileCreateSession(workspaceId: String, kind: String, provider: String) async throws -> String { lock.lock(); commands.append("create:\(workspaceId):\(kind):\(provider)"); lock.unlock(); return "session-2" }
    func bump(state: Bool, session: Bool) { lock.lock(); if state { stateRevision += 1 }; if session { sessionRevision += 1 }; lock.unlock() }
    func recorded() -> [String] { lock.lock(); defer { lock.unlock() }; return commands }
}

struct MobileRemoteTests {
    private func request(_ status: MobileHostStatus, _ method: String, _ path: String, token: String? = nil, body: [String: Any]? = nil, headers: [String: String] = [:]) async throws -> (Int, [String: Any]) {
        let key = token ?? status.key ?? ""
        var request = URLRequest(url: URL(string: status.address! + path)!)
        request.httpMethod = method
        request.setValue("Bearer " + key, forHTTPHeaderField: "Authorization")
        request.setValue("1", forHTTPHeaderField: "x-mighty-mobile-version")
        for (name, value) in headers { request.setValue(value, forHTTPHeaderField: name) }
        if let body {
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        request.timeoutInterval = 15
        let (data, response) = try await URLSession.shared.data(for: request)
        let object = (try? JSONSerialization.jsonObject(with: data) as? [String: Any]) ?? [:]
        return ((response as? HTTPURLResponse)?.statusCode ?? 0, object)
    }

    @Test func routesAuthenticateValidateAndForwardCommands() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mobile-remote-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let service = MobileRemoteService(dataDirectory: directory, hostName: "Test Mac", appVersion: "9.9.9", allowLoopbackForTests: true)
        let host = FakeMobileHost()
        await service.attach(host)
        let status = await service.apply(settings: MobileRemoteSettings(enabled: true, port: 0))
        #expect(status.listening && status.enabled && status.key != nil && status.pairingURL?.hasPrefix("mightyclaude://pair?v=1&host=127.0.0.1&port=") == true)
        defer { Task { await service.shutdown() } }

        // Auth: wrong key, missing version, browser origin.
        #expect(try await request(status, "GET", "/m1/info", token: String(repeating: "x", count: 43)).0 == 401)
        #expect(try await request(status, "GET", "/m1/info", headers: ["x-mighty-mobile-version": "2"]).0 == 426)
        #expect(try await request(status, "GET", "/m1/info", headers: ["Origin": "http://evil"]).0 == 403)
        let info = try await request(status, "GET", "/m1/info")
        #expect(info.0 == 200 && info.1["hostName"] as? String == "Test Mac" && info.1["appVersion"] as? String == "9.9.9" && info.1["platform"] as? String == "darwin")

        // State: immediate when the revision is newer, long-poll otherwise.
        let state = try await request(status, "GET", "/m1/state?since=0&wait=0")
        #expect(state.0 == 200 && state.1["revision"] as? Int == 1 && (state.1["sessions"] as? [[String: Any]])?.first?["pendingPermissions"] as? Int == 1)
        let started = Date()
        async let waiting = request(status, "GET", "/m1/state?since=1&wait=5")
        try await Task.sleep(for: .milliseconds(300))
        host.bump(state: true, session: false); await service.notify(scope: "state", revision: 2)
        let woken = try await waiting
        #expect(woken.0 == 200 && woken.1["revision"] as? Int == 2 && Date().timeIntervalSince(started) < 4)
        let timedOut = try await request(status, "GET", "/m1/state?since=2&wait=1")
        #expect(timedOut.1["revision"] as? Int == 2)
        #expect(try await request(status, "GET", "/m1/state?since=abc").0 == 400)

        // Session detail carries the structured permission card.
        let detail = try await request(status, "GET", "/m1/sessions/session-1?since=0")
        let permission = (detail.1["permissions"] as? [[String: Any]])?.first
        #expect(detail.0 == 200 && permission?["title"] as? String == "명령 실행" && permission?["headline"] as? String == "Run the test suite")
        #expect((detail.1["entries"] as? [[String: Any]])?.count == 1 && (detail.1["usage"] as? [String: Any])?["contextPercent"] as? Double == 12.5)
        #expect(try await request(status, "GET", "/m1/sessions/missing?since=0").0 == 404)

        // Commands.
        #expect(try await request(status, "POST", "/m1/sessions/session-1/submit", body: ["text": "  테스트 추가해줘 "]).1["accepted"] as? String == "steered")
        #expect(try await request(status, "POST", "/m1/sessions/session-1/submit", body: ["text": "   "]).0 == 400)
        host.failSubmit = true
        let refused = try await request(status, "POST", "/m1/sessions/session-1/submit", body: ["text": "x"])
        #expect(refused.0 == 409 && (refused.1["error"] as? String)?.contains("실행 준비") == true)
        host.failSubmit = false
        #expect(try await request(status, "POST", "/m1/sessions/session-1/stop").0 == 200)
        #expect(try await request(status, "POST", "/m1/sessions/session-1/permission", body: ["requestId": "perm-1", "runId": "run-1", "allow": true]).0 == 200)
        #expect(try await request(status, "POST", "/m1/sessions/session-1/permission", body: ["requestId": "../x", "runId": "run-1", "allow": true]).0 == 400)
        #expect(try await request(status, "POST", "/m1/sessions/session-1/answers", body: ["requestId": "ask-1", "runId": "run-1", "answers": ["어느 쪽?": ["selectedOptions": ["A"]]]]).0 == 200)
        let created = try await request(status, "POST", "/m1/workspaces/workspace-1/sessions", body: ["kind": "claude", "provider": "codex"])
        #expect(created.0 == 201 && created.1["sessionId"] as? String == "session-2")
        #expect(try await request(status, "POST", "/m1/workspaces/workspace-1/sessions", body: ["kind": "browser"]).0 == 400)
        #expect(try await request(status, "POST", "/m1/nothing").0 == 404)
        #expect(host.recorded() == ["submit:session-1:테스트 추가해줘", "stop:session-1", "perm:perm-1:run-1:true", "answers:ask-1:어느 쪽?", "create:workspace-1:claude:codex"])

        // The key survives a restart of the service; regenerating it revokes the old one.
        let again = MobileRemoteService(dataDirectory: directory, hostName: "Test Mac", allowLoopbackForTests: true)
        #expect(try await again.loadOrCreateKey() == status.key)
        let oldKey = status.key!
        _ = try await service.regenerateKey()
        let rotated = await service.apply(settings: MobileRemoteSettings(enabled: true, port: 0))
        #expect(rotated.key != oldKey)
        let revoked = try await request(rotated, "GET", "/m1/info", token: oldKey)
        #expect(revoked.0 == 401, "rotated=\(rotated) revoked=\(revoked)")
        let accepted = try await request(rotated, "GET", "/m1/info")
        #expect(accepted.0 == 200, "accepted=\(accepted)")
        let attributes = try FileManager.default.attributesOfItem(atPath: directory.appendingPathComponent("mobile-remote.key").path)
        #expect((attributes[.posixPermissions] as? Int) == 0o600)
        let off = await service.apply(settings: MobileRemoteSettings(enabled: false, port: 0))
        #expect(!off.listening && off.key == nil)
    }

    @Test func settingsAndPairingNormalize() throws {
        #expect(MobileRemoteSettings(enabled: true, port: 80).normalized.port == MobileRemoteSettings.defaultPort)
        #expect(MobileRemoteSettings(enabled: true, port: 50000).normalized.port == 50000)
        let url = MobilePairing.url(host: "100.64.1.2", port: 43138, key: "abc_-", name: "Young의 Mac")
        #expect(url.hasPrefix("mightyclaude://pair?v=1&host=100.64.1.2&port=43138&key=abc_-&name=Young"))
        let snapshot = AppSnapshot(mobileRemote: MobileRemoteSettings(enabled: true, port: 22))
        let normalized = StateRepository.normalize(snapshot, restoring: true)
        #expect(normalized.mobileRemote == MobileRemoteSettings(enabled: true, port: MobileRemoteSettings.defaultPort))
        let data = try JSONEncoder().encode(normalized)
        #expect(StateRepository.decodeSnapshot(data).mobileRemote?.enabled == true)
        #expect(StateRepository.decodeSnapshot(Data(#"{"workspaces":[],"sessions":[]}"#.utf8)).mobileRemote == nil)
    }
}
