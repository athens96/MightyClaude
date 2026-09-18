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
    private func call(_ service: MobileRemoteService, _ method: String, _ path: String, body: [String: Any]? = nil) async throws -> (Int, [String: Any]) {
        let data = try body.map { try JSONSerialization.data(withJSONObject: $0) }
        let reply = await service.route(method: method, path: path, body: data)
        let object = (try? JSONSerialization.jsonObject(with: reply.body) as? [String: Any]) ?? [:]
        return (reply.status, object)
    }

    @Test func routesValidateAndForwardCommandsAndLongPollWakesOnNotify() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mobile-remote-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let service = MobileRemoteService(dataDirectory: directory, hostName: "Test Mac", appVersion: "9.9.9")
        let host = FakeMobileHost()
        await service.attach(host)

        let info = try await call(service, "GET", "/m1/info")
        #expect(info.0 == 200 && info.1["hostName"] as? String == "Test Mac" && info.1["appVersion"] as? String == "9.9.9" && info.1["platform"] as? String == "darwin")
        let state = try await call(service, "GET", "/m1/state?since=0&wait=0")
        #expect(state.0 == 200 && state.1["revision"] as? Int == 1 && (state.1["sessions"] as? [[String: Any]])?.first?["pendingPermissions"] as? Int == 1)
        // Long-poll: blocks until notify raises the revision, well before `wait`.
        let started = Date()
        async let waiting = call(service, "GET", "/m1/state?since=1&wait=5")
        try await Task.sleep(for: .milliseconds(300))
        host.bump(state: true, session: false); await service.notify(scope: "state", revision: 2)
        let woken = try await waiting
        #expect(woken.0 == 200 && woken.1["revision"] as? Int == 2 && Date().timeIntervalSince(started) < 4)
        let timedOut = try await call(service, "GET", "/m1/state?since=2&wait=1")
        #expect(timedOut.1["revision"] as? Int == 2)
        #expect(try await call(service, "GET", "/m1/state?since=abc").0 == 400)
        #expect(try await call(service, "GET", "/m1/state?since=1&other=1").0 == 400)

        let detail = try await call(service, "GET", "/m1/sessions/session-1?since=0")
        let permission = (detail.1["permissions"] as? [[String: Any]])?.first
        #expect(detail.0 == 200 && permission?["title"] as? String == "명령 실행" && permission?["headline"] as? String == "Run the test suite")
        #expect((detail.1["entries"] as? [[String: Any]])?.count == 1 && (detail.1["usage"] as? [String: Any])?["contextPercent"] as? Double == 12.5)
        #expect(try await call(service, "GET", "/m1/sessions/missing?since=0").0 == 404)
        #expect(try await call(service, "GET", "/m1/sessions/../x").0 == 404)

        #expect(try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "  테스트 추가해줘 "]).1["accepted"] as? String == "steered")
        #expect(try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "   "]).0 == 400)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/submit").0 == 400)
        host.failSubmit = true
        let refused = try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "x"])
        #expect(refused.0 == 409 && (refused.1["error"] as? String)?.contains("실행 준비") == true)
        host.failSubmit = false
        let stopped = try await call(service, "POST", "/m1/sessions/session-1/stop")
        #expect(stopped.0 == 200 && stopped.1["stopped"] as? Bool == true)
        let allowed = try await call(service, "POST", "/m1/sessions/session-1/permission", body: ["requestId": "perm-1", "runId": "run-1", "allow": true])
        #expect(allowed.0 == 200 && allowed.1["ok"] as? Bool == true)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/permission", body: ["requestId": "../x", "runId": "run-1", "allow": true]).0 == 400)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/answers", body: ["requestId": "ask-1", "runId": "run-1", "answers": ["어느 쪽?": ["selectedOptions": ["A"]]]]).0 == 200)
        let created = try await call(service, "POST", "/m1/workspaces/workspace-1/sessions", body: ["kind": "claude", "provider": "codex"])
        #expect(created.0 == 201 && created.1["sessionId"] as? String == "session-2")
        #expect(try await call(service, "POST", "/m1/workspaces/workspace-1/sessions", body: ["kind": "browser"]).0 == 400)
        #expect(try await call(service, "POST", "/m1/nothing").0 == 404)
        #expect(try await call(service, "GET", "http://evil/m1/info").0 == 400)
        #expect(host.recorded() == ["submit:session-1:테스트 추가해줘", "stop:session-1", "perm:perm-1:run-1:true", "answers:ask-1:어느 쪽?", "create:workspace-1:claude:codex"])
    }

    @Test func keysPersistAndStatusExposesTheOfferOnlyWhileConnected() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mobile-remote-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let service = MobileRemoteService(dataDirectory: directory, hostName: "Test Mac")
        let key = try await service.loadOrCreateKey()
        #expect(RemoteValidation.token(key))
        let reloaded = try await MobileRemoteService(dataDirectory: directory, hostName: "Test Mac").loadOrCreateKey()
        #expect(reloaded == key)
        #expect((try FileManager.default.attributesOfItem(atPath: directory.appendingPathComponent("mobile-remote.key").path)[.posixPermissions] as? Int) == 0o600)
        let rotated = try await service.regenerateKey()
        let afterRotation = try await service.loadOrCreateKey()
        #expect(rotated != key && afterRotation == rotated)

        // Off: no offer. On without a relay: still no offer, but a hint.
        var status = await service.apply(settings: MobileRemoteSettings(enabled: false))
        #expect(!status.enabled && status.pairingURL == nil && status.publicKeyB64?.isEmpty == false)
        status = await service.apply(settings: MobileRemoteSettings(enabled: true, relayURL: ""))
        #expect(status.enabled && !status.relayConnected && status.pairingURL == nil && status.detail.contains("릴레이 주소"))
        // On with an unreachable relay: connecting, still no offer until the relay accepts.
        status = await service.apply(settings: MobileRemoteSettings(enabled: true, relayURL: "ws://127.0.0.1:1"))
        #expect(status.enabled && status.relayURL == "ws://127.0.0.1:1" && status.pairingURL == nil)
        let sameHostId = await MobileRemoteService(dataDirectory: directory, hostName: "Test Mac").status().serverId
        #expect(sameHostId == status.serverId && CoreValidation.identifier(status.serverId))
        await service.shutdown()
    }
}
