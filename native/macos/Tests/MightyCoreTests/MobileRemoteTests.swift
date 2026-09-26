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
    var runNextFails = false
    /// The pane is busy, so a submit defers instead of starting.
    var running = true
    /// What the steer chain finally reported. The runner may refuse the text
    /// and settling the queue may start it at once, so all four are possible.
    var steerEffect = SubmitOutcome.steered
    /// The pane's view mode; guided styles exist only inside Mighty view.
    var viewMode = MobileWire.plainViewMode
    /// The guided style this pane is in; a `guided` request for another one 409s.
    var paneStyle = MightyStyleIDs.ouroboros
    /// Non-nil when the pane cannot run at all (CLI updating, no connection).
    var blockedReason: String?
    /// Twelve saved entries, so paging has something to walk back through.
    let history = (1...12).map { LogEntry(id: "entry-\($0)", kind: "assistant", text: "줄 \($0)") }
    static func options(guidedStyles: Bool) -> MobileSettingsOptions {
        MobileSettingsOptions(
            models: [MobileOption(id: "default", label: "CLI 기본값"), MobileOption(id: "opus", label: "opus")],
            permissionModes: [MobileOption(id: "manual", label: "Always ask"), MobileOption(id: "plan", label: "Plan mode")],
            efforts: [MobileOption(id: "default", label: "Auto"), MobileOption(id: "high", label: "High")],
            mightyStyles: (guidedStyles ? MobileWire.mightyStyles : [MobileWire.cliStyle]).map { MobileOption(id: $0, label: $0) },
            styles: MobileRemoteSupport.styleOptions(guidedStyles ? BundledStyles.shared.styles() : []))
    }
    private func summary() -> MobileSessionSummary {
        MobileSessionSummary(id: "session-1", workspaceId: "workspace-1", title: "Claude", kind: "claude", provider: "claude", model: "default",
                             status: running ? "running" : "idle",
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
    /// How long the pane takes to accept a submit, so two requests naming the
    /// same upload can be made to overlap on purpose.
    var submitDelayMilliseconds = 0
    /// The most submits that were ever inside the pane at once.
    private var liveSubmits = 0
    var peakSubmits = 0
    func mobileSubmit(sessionId: String, text: String, mode: String?, attachments: [RunAttachment]) async throws -> String {
        lock.lock()
        let delay = submitDelayMilliseconds
        liveSubmits += 1; peakSubmits = max(peakSubmits, liveSubmits)
        lock.unlock()
        if delay > 0 { try? await Task.sleep(for: .milliseconds(delay)) }
        lock.lock(); defer { liveSubmits -= 1; lock.unlock() }
        if failSubmit { throw MightyError("실행 준비가 필요합니다.") }
        let files = attachments.map(\.name).joined(separator: ",")
        commands.append("submit:\(sessionId):\(text)" + (files.isEmpty ? "" : ":" + files))
        // Idle: `mode` only means "do not steer", and nothing would drain a
        // queue here, so the run starts either way.
        var effect = SubmitOutcome.started
        // Files cannot ride a steer, so a busy pane queues them however the
        // phone asked — the same rule the store applies.
        if running { effect = (mode == "queue" || !attachments.isEmpty) ? .queued : steerEffect }
        guard let accepted = effect.accepted else { throw MobileHostError.conflict(MobileRemoteSupport.droppedMessage) }
        return accepted
    }
    func mobileGuided(sessionId: String, style: String, skill: String, text: String) async throws -> String {
        lock.lock(); defer { lock.unlock() }
        try known(sessionId)
        // The registry the host would consult; the two bundled styles are the
        // only ones this scripted app knows.
        guard let registered = BundledStyles.shared.style(style) else { throw MobileHostError.badRequest(MobileRemoteSupport.unknownStyleMessage) }
        guard style == paneStyle else { throw MobileHostError.conflict("이 실행 창은 \(style) 스타일이 아닙니다.") }
        guard let prompt = MobileMightySupport.guidedPrompt(registered, actionId: skill, text: text) else {
            throw MobileHostError.badRequest("이 스타일에 없는 스킬입니다.")
        }
        commands.append("guided:\(prompt)")
        return running ? "queued" : "started"
    }
    func mobileStop(sessionId: String) async throws { lock.lock(); commands.append("stop:\(sessionId)"); lock.unlock() }
    func mobilePermission(sessionId: String, requestId: String, runId: String, allow: Bool) async throws { lock.lock(); commands.append("perm:\(requestId):\(runId):\(allow)"); lock.unlock() }
    func mobileAnswers(sessionId: String, requestId: String, runId: String, answers: [String: UserQuestionAnswer]) async throws {
        lock.lock(); commands.append("answers:\(requestId):\(answers.keys.sorted().joined(separator: ","))"); lock.unlock()
    }
    func mobileCreateSession(workspaceId: String, kind: String, provider: String) async throws -> String {
        lock.lock(); defer { lock.unlock() }
        if kind == "shell" { throw MobileHostError.conflict("로컬 워크스페이스의 명령 창은 휴대폰에서 쓸 수 없습니다.") }
        commands.append("create:\(workspaceId):\(kind):\(provider)"); return "session-2"
    }
    /// Every extension route refuses an unknown pane with 404, as the host does.
    private func known(_ sessionId: String) throws {
        guard sessionId == "session-1" else { throw MobileHostError.notFound("실행 창을 찾을 수 없습니다.") }
    }
    func mobileRemoveQueued(sessionId: String, itemId: String) async throws {
        lock.lock(); defer { lock.unlock() }
        try known(sessionId)
        guard itemId == "q1" else { throw MobileHostError.notFound("대기 중인 항목을 찾을 수 없습니다.") }
        commands.append("remove:\(sessionId):\(itemId)")
    }
    func mobileRunNextQueued(sessionId: String) async throws {
        lock.lock(); defer { lock.unlock() }
        try known(sessionId)
        if runNextFails { throw MobileHostError.conflict("실행이 끝난 뒤에 다음 요청을 시작할 수 있습니다.") }
        // Checked before settling: settling a blocked pane discards the whole
        // queue and would still answer ok.
        if let blockedReason { throw MobileHostError.conflict(blockedReason) }
        commands.append("run-next:\(sessionId)")
    }
    func mobileRename(sessionId: String, title: String) async throws {
        lock.lock(); defer { lock.unlock() }
        try known(sessionId); commands.append("rename:\(title)")
    }
    func mobileClose(sessionId: String) async throws {
        lock.lock(); defer { lock.unlock() }
        try known(sessionId); commands.append("close:\(sessionId)")
    }
    func mobileEntries(sessionId: String, before: String, limit: Int) async throws -> MobileEntriesPage {
        lock.lock(); defer { lock.unlock() }
        try known(sessionId)
        commands.append("entries:\(before):\(limit)")
        let page = MobileRemoteSupport.page(entries: history, before: before, limit: limit)
        return MobileEntriesPage(entries: page.entries, hasMore: page.hasMore)
    }
    func mobileApplySettings(sessionId: String, request: MobileSettingsRequest) async throws {
        lock.lock(); defer { lock.unlock() }
        try known(sessionId)
        // Shape before state, as the host does, and the style is judged against
        // the view mode this same request asks for.
        let mode = request.agentViewMode ?? viewMode
        let guided = MobileRemoteSupport.guidedStylesAvailable(kind: "claude", provider: "claude", localWorkspace: true, viewMode: mode)
        try MobileRemoteSupport.validate(request, options: Self.options(guidedStyles: guided))
        if running { throw MobileHostError.conflict("실행 중에는 설정을 바꿀 수 없습니다.") }
        if let value = request.agentViewMode { viewMode = value }
        commands.append("settings:\(request.model ?? "-"):\(request.agentViewMode ?? "-"):\(request.mightyStyle ?? "-")")
    }
    func mobileCommands(sessionId: String) async throws -> [MobileCommand] {
        lock.lock(); defer { lock.unlock() }
        try known(sessionId)
        return MobileCommandSupport.wire(SlashCommandCatalog.builtins(provider: "claude"))
    }
    func mobilePerformCommand(sessionId: String, action: String) async throws -> String? {
        lock.lock(); defer { lock.unlock() }
        try known(sessionId)
        commands.append("command:\(action)")
        return action == "clear" ? nil : "본문 " + action
    }
    func bump(state: Bool, session: Bool) { lock.lock(); if state { stateRevision += 1 }; if session { sessionRevision += 1 }; lock.unlock() }
    func recorded() -> [String] { lock.lock(); defer { lock.unlock() }; return commands }
}

struct MobileRemoteTests {
    /// The device a routed request arrives as. Uploads belong to it, so the
    /// tests that cross devices name a second one explicitly.
    private static let phone = "cGhvbmUtb25lLTAwMDAwMDA"
    private static let other = "cGhvbmUtdHdvLTAwMDAwMDA"

    private func call(_ service: MobileRemoteService, _ method: String, _ path: String, body: [String: Any]? = nil,
                      device: String = MobileRemoteTests.phone) async throws -> (Int, [String: Any]) {
        let data = try body.map { try JSONSerialization.data(withJSONObject: $0) }
        let reply = await service.route(method: method, path: path, body: data, deviceId: device)
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

    private func service(_ host: FakeMobileHost) async -> (MobileRemoteService, URL) {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mobile-remote-" + UUID().uuidString, isDirectory: true)
        let service = MobileRemoteService(dataDirectory: directory, hostName: "Test Mac", appVersion: "9.9.9")
        await service.attach(host)
        return (service, directory)
    }

    @Test func infoAdvertisesOnlyTheCapabilitiesThisHostImplements() async throws {
        let host = FakeMobileHost()
        let (service, directory) = await service(host)
        defer { try? FileManager.default.removeItem(at: directory) }
        let info = try await call(service, "GET", "/m1/info")
        // The set, not the order: the phone looks names up, it does not index.
        let advertised = Set(info.1["capabilities"] as? [String] ?? [])
        #expect(advertised == Set(MobileCapability.all))
        // Every name here is a route this host serves; nothing is promised early.
        #expect(advertised.isSuperset(of: ["mighty", "attachments"]))
        #expect(advertised.isDisjoint(with: ["notifications", "terminal"]))
    }

    @Test func theGuidedRouteBuildsTheMacsOwnPromptAndRefusesTheRest() async throws {
        let host = FakeMobileHost()
        let (service, directory) = await service(host)
        defer { try? FileManager.default.removeItem(at: directory) }
        let sent = try await call(service, "POST", "/m1/sessions/session-1/guided", body: ["style": "ouroboros", "skill": "interview", "text": "결제 흐름 정리"])
        #expect(sent.0 == 202 && sent.1["accepted"] as? String == "queued")
        // A skill that does not take text sends the bare prompt.
        #expect(try await call(service, "POST", "/m1/sessions/session-1/guided", body: ["style": "ouroboros", "skill": "seed", "text": "무시됨"]).0 == 202)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/guided", body: ["style": "ouroboros", "skill": "nope"]).0 == 400)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/guided", body: ["style": "cli", "skill": "interview"]).0 == 400)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/guided", body: ["style": "ouroboros", "skill": "../etc"]).0 == 400)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/guided", body: ["skill": "interview"]).0 == 400)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/guided", body: ["style": "ouroboros", "skill": "interview", "text": String(repeating: "가", count: 11_000)]).0 == 400)
        // The pane is in the other style: nothing is sent.
        #expect(try await call(service, "POST", "/m1/sessions/session-1/guided", body: ["style": "paperthin", "skill": "re0"]).0 == 409)
        #expect(try await call(service, "POST", "/m1/sessions/missing/guided", body: ["style": "ouroboros", "skill": "seed"]).0 == 404)
        host.paneStyle = MightyStyleIDs.paperthin
        #expect(try await call(service, "POST", "/m1/sessions/session-1/guided", body: ["style": "paperthin", "skill": "re0", "text": "docs/spec.md"]).0 == 202)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/guided", body: ["style": "paperthin", "skill": "interview"]).0 == 400)
        #expect(host.recorded() == ["guided:/ouroboros:interview 결제 흐름 정리", "guided:/ouroboros:seed", "guided:/re0 docs/spec.md"])
    }

    @Test func theGuidedRouteTakesTheNewShapeAndHidesUnknownStylesAlike() async throws {
        let host = FakeMobileHost()
        let (service, directory) = await service(host)
        defer { try? FileManager.default.removeItem(at: directory) }
        // The new shape reaches the same place as the old one (§7.5).
        #expect(try await call(service, "POST", "/m1/sessions/session-1/guided", body: ["styleId": "ouroboros", "actionId": "interview", "text": "목표"]).0 == 202)
        // Both shapes together: the new one wins, so the old skill is ignored.
        #expect(try await call(service, "POST", "/m1/sessions/session-1/guided", body: ["styleId": "ouroboros", "actionId": "seed", "style": "paperthin", "skill": "re0"]).0 == 202)
        #expect(host.recorded() == ["guided:/ouroboros:interview 목표", "guided:/ouroboros:seed"])
        // The wire word for "no style" and a malformed id answer alike, so a
        // style the host does not run is not told apart from one that is not
        // there at all (§4.5). Whether a registered-but-unapproved id reaches
        // the same answer is the host's own judgement, asserted where it lives.
        for body in [["styleId": "cli", "actionId": "interview"], ["styleId": "Bad_Shape", "actionId": "interview"],
                     ["styleId": String(repeating: "a", count: 41), "actionId": "interview"]] {
            let reply = try await call(service, "POST", "/m1/sessions/session-1/guided", body: body)
            #expect(reply.0 == 400 && reply.1["error"] as? String == MobileRemoteSupport.unknownStyleMessage)
        }
        // An id the host does run but a pane that is not in it stays a 409.
        #expect(try await call(service, "POST", "/m1/sessions/session-1/guided", body: ["styleId": "paperthin", "actionId": "re0"]).0 == 409)
    }

    @Test func uploadsTravelInOrderAndOnlyACompleteOneCanBeSubmitted() async throws {
        let host = FakeMobileHost()
        host.running = false
        let (service, directory) = await service(host)
        defer { try? FileManager.default.removeItem(at: directory) }
        let payload = Data(("%PDF-1.4\n" + String(repeating: "a", count: 200_000)).utf8)
        let opened = try await call(service, "POST", "/m1/sessions/session-1/uploads", body: ["name": "../../report.pdf", "size": payload.count])
        #expect(opened.0 == 201 && opened.1["chunkSize"] as? Int == 196_608)
        let uploadId = try #require(opened.1["uploadId"] as? String)
        let first = payload.prefix(196_608), second = payload.suffix(from: 196_608)
        // Out of order in both directions, before anything has been accepted.
        #expect(try await call(service, "POST", "/m1/uploads/\(uploadId)/chunks/1", body: ["dataBase64": second.base64EncodedString()]).0 == 409)
        #expect(try await call(service, "POST", "/m1/uploads/\(uploadId)/chunks/9", body: ["dataBase64": second.base64EncodedString()]).0 == 400)
        let chunk = try await call(service, "POST", "/m1/uploads/\(uploadId)/chunks/0", body: ["dataBase64": first.base64EncodedString()])
        #expect(chunk.0 == 200 && chunk.1["received"] as? Int == 196_608)
        // The same chunk twice is a conflict, and the tail must be exactly what
        // is left of the declared size.
        #expect(try await call(service, "POST", "/m1/uploads/\(uploadId)/chunks/0", body: ["dataBase64": first.base64EncodedString()]).0 == 409)
        #expect(try await call(service, "POST", "/m1/uploads/\(uploadId)/complete").0 == 400)
        #expect(try await call(service, "POST", "/m1/uploads/\(uploadId)/chunks/1", body: ["dataBase64": second.dropLast().base64EncodedString()]).0 == 400)
        #expect(try await call(service, "POST", "/m1/uploads/\(uploadId)/chunks/1", body: ["dataBase64": "not base64!!"]).0 == 400)
        #expect(try await call(service, "POST", "/m1/uploads/\(uploadId)/chunks/1", body: ["dataBase64": second.base64EncodedString()]).0 == 200)
        let done = try await call(service, "POST", "/m1/uploads/\(uploadId)/complete")
        let attachment = done.1["attachment"] as? [String: Any]
        // The phone's path separators never reach the name.
        #expect(done.0 == 200 && attachment?["name"] as? String == "report.pdf" && attachment?["size"] as? Int == payload.count)
        #expect(try await call(service, "POST", "/m1/uploads/\(uploadId)/complete").0 == 409)
        let submitted = try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "이 문서 읽어줘", "attachments": [uploadId]])
        #expect(submitted.0 == 202 && submitted.1["accepted"] as? String == "started")
        // Spent: naming it again is a malformed request, not a second copy.
        #expect(try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "다시", "attachments": [uploadId]]).0 == 400)
        #expect(host.recorded() == ["submit:session-1:이 문서 읽어줘:report.pdf"])
    }

    @Test func uploadRoutesRefuseWhatTheContractForbids() async throws {
        let host = FakeMobileHost()
        let (service, directory) = await service(host)
        defer { try? FileManager.default.removeItem(at: directory) }
        #expect(try await call(service, "POST", "/m1/sessions/session-1/uploads", body: ["name": "big.bin", "size": 6 * 1024 * 1024]).0 == 413)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/uploads", body: ["name": "empty.bin", "size": 0]).0 == 400)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/uploads", body: ["name": "...", "size": 10]).0 == 400)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/uploads", body: ["name": "a.bin"]).0 == 400)
        #expect(try await call(service, "POST", "/m1/sessions/missing/uploads", body: ["name": "a.bin", "size": 10]).0 == 404)
        #expect(try await call(service, "POST", "/m1/uploads/nope/complete").0 == 404)
        #expect(try await call(service, "POST", "/m1/uploads/..%2Fetc/cancel").0 == 404)
        #expect(try await call(service, "POST", "/m1/uploads/nope/chunks/0", body: ["dataBase64": ""]).0 == 404)
        // A submit naming an upload nobody opened is malformed, not a 404 pane.
        #expect(try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "x", "attachments": ["nope"]]).0 == 400)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "x", "attachments": ["../etc"]]).0 == 400)
        let many = (1...9).map { "upload-\($0)" }
        #expect(try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "x", "attachments": many]).0 == 413)
        // Text may be empty when files carry the request, never otherwise.
        #expect(try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": " "]).0 == 400)
        let opened = try await call(service, "POST", "/m1/sessions/session-1/uploads", body: ["name": "note.txt", "size": 4])
        let uploadId = try #require(opened.1["uploadId"] as? String)
        #expect(try await call(service, "POST", "/m1/uploads/\(uploadId)/cancel").0 == 200)
        #expect(try await call(service, "POST", "/m1/uploads/\(uploadId)/cancel").0 == 404)
        // Sixteen open uploads per pane, and then 429 until one is finished or
        // cancelled — a "come back later", not a size refusal.
        for index in 0..<MobileUploadStore.maximumOpenPerSession {
            #expect(try await call(service, "POST", "/m1/sessions/session-1/uploads", body: ["name": "f\(index).bin", "size": 4]).0 == 201)
        }
        #expect(try await call(service, "POST", "/m1/sessions/session-1/uploads", body: ["name": "over.bin", "size": 4]).0 == 429)
        // Closing the pane hands its slots back.
        #expect(try await call(service, "POST", "/m1/sessions/session-1/close").0 == 200)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/uploads", body: ["name": "after.bin", "size": 4]).0 == 201)
    }

    /// The chunk route carries a base64 chunk and so has its own 300 KiB body
    /// limit; every other route keeps the protocol's 64 KiB one.
    @Test func onlyTheChunkRouteAcceptsABodyLargerThanSixtyFourKiB() async throws {
        let host = FakeMobileHost()
        host.running = false
        let (service, directory) = await service(host)
        defer { try? FileManager.default.removeItem(at: directory) }
        // Past 64 KiB on an ordinary route the body is refused before it is read.
        #expect(try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": String(repeating: "a", count: MobileRemoteService.bodyLimit + 1)]).0 == 413)
        // Just inside it the body is read, and the text's own 32 KiB ceiling
        // answers — proof the 413 above came from the body limit, not the text.
        #expect(try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": String(repeating: "a", count: MobileRemoteService.bodyLimit - 64)]).0 == 400)

        let payload = Data(repeating: 67, count: MobileUploadStore.chunkSize)
        let opened = try await call(service, "POST", "/m1/sessions/session-1/uploads", body: ["name": "big.bin", "size": payload.count])
        let uploadId = try #require(opened.1["uploadId"] as? String)
        let encoded = payload.base64EncodedString()
        // A full chunk is four times 64 KiB once base64-encoded, and lands.
        #expect(encoded.utf8.count > MobileRemoteService.bodyLimit && encoded.utf8.count < MobileUploadStore.chunkBodyLimit)
        #expect(try await call(service, "POST", "/m1/uploads/\(uploadId)/chunks/0", body: ["dataBase64": encoded]).0 == 200)
        // Beyond this route's own limit it is refused just the same.
        let oversized = String(repeating: "A", count: MobileUploadStore.chunkBodyLimit + 1)
        #expect(try await call(service, "POST", "/m1/uploads/\(uploadId)/chunks/1", body: ["dataBase64": oversized]).0 == 413)
    }

    @Test func submitReportsWhatActuallyHappenedAndRefusesAnUnknownMode() async throws {
        let host = FakeMobileHost()
        let (service, directory) = await service(host)
        defer { try? FileManager.default.removeItem(at: directory) }
        let steered = try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "이어서", "mode": "steer"])
        #expect(steered.0 == 202 && steered.1["accepted"] as? String == "steered")
        let queued = try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "이어서", "mode": "queue"])
        #expect(queued.0 == 202 && queued.1["accepted"] as? String == "queued")
        let omitted = try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "이어서"])
        #expect(omitted.1["accepted"] as? String == "steered")
        // The runner refused the text and settling the queue started it at once.
        host.steerEffect = .started
        let restarted = try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "이어서"])
        #expect(restarted.1["accepted"] as? String == "started")
        // The turn had closed: the item waits instead of joining it.
        host.steerEffect = .queued
        let deferred = try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "이어서"])
        #expect(deferred.1["accepted"] as? String == "queued")
        // Neither steered nor queued: a refusal, never a queued item that is not there.
        host.steerEffect = .dropped
        let dropped = try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "이어서"])
        #expect(dropped.0 == 409 && dropped.1["accepted"] == nil)
        #expect(dropped.1["error"] as? String == MobileRemoteSupport.droppedMessage)
        host.steerEffect = .steered
        #expect(try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "x", "mode": "later"]).0 == 400)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "x", "mode": ""]).0 == 400)
        host.failSubmit = true
        #expect(try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "x", "mode": "queue"]).0 == 409)
    }

    @Test func anIdlePaneStartsTheRunWhateverTheModeAsksFor() async throws {
        let host = FakeMobileHost()
        host.running = false
        let (service, directory) = await service(host)
        defer { try? FileManager.default.removeItem(at: directory) }
        // `mode` only says "do not steer". Nothing would ever drain a queue on
        // an idle pane, so the request starts the run and says so.
        for mode in ["queue", "steer"] {
            let reply = try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "시작", "mode": mode])
            #expect(reply.0 == 202 && reply.1["accepted"] as? String == "started")
        }
        let omitted = try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "시작"])
        #expect(omitted.1["accepted"] as? String == "started")
    }

    @Test func queueRoutesRemoveAndRunTheNextItem() async throws {
        let host = FakeMobileHost()
        let (service, directory) = await service(host)
        defer { try? FileManager.default.removeItem(at: directory) }
        #expect(try await call(service, "POST", "/m1/sessions/session-1/queue/q1/remove").0 == 200)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/queue/q9/remove").0 == 404)
        #expect(try await call(service, "POST", "/m1/sessions/missing/queue/q1/remove").0 == 404)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/queue/..%2Fx/remove").0 == 404)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/queue/run-next").0 == 200)
        // A pane that cannot run is refused with the reason, not answered "ok"
        // while the queue is thrown away behind the phone's back.
        host.blockedReason = "Claude CLI를 업데이트하고 있습니다. 완료 후 전송하세요."
        let blocked = try await call(service, "POST", "/m1/sessions/session-1/queue/run-next")
        #expect(blocked.0 == 409 && (blocked.1["error"] as? String)?.contains("업데이트") == true)
        host.blockedReason = nil
        host.runNextFails = true
        #expect(try await call(service, "POST", "/m1/sessions/session-1/queue/run-next").0 == 409)
        #expect(try await call(service, "POST", "/m1/sessions/missing/queue/run-next").0 == 404)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/queue/q1/drop").0 == 404)
        #expect(host.recorded() == ["remove:session-1:q1", "run-next:session-1"])
    }

    @Test func renameChecksItsBoundsAndCloseIsAcknowledged() async throws {
        let host = FakeMobileHost()
        let (service, directory) = await service(host)
        defer { try? FileManager.default.removeItem(at: directory) }
        #expect(try await call(service, "POST", "/m1/sessions/session-1/rename", body: ["title": "  릴리스 준비  "]).0 == 200)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/rename", body: ["title": "   "]).0 == 400)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/rename", body: ["title": String(repeating: "가", count: 81)]).0 == 400)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/rename", body: [:]).0 == 400)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/rename").0 == 400)
        let long = try await call(service, "POST", "/m1/sessions/session-1/rename", body: ["title": String(repeating: "가", count: 80)])
        #expect(long.0 == 200)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/close").0 == 200)
        #expect(try await call(service, "POST", "/m1/sessions/missing/close").0 == 404)
        #expect(host.recorded() == ["rename:릴리스 준비", "rename:" + String(repeating: "가", count: 80), "close:session-1"])
    }

    @Test func theEntriesRouteForwardsItsCursorAndChecksItsQuery() async throws {
        let host = FakeMobileHost()
        let (service, directory) = await service(host)
        defer { try? FileManager.default.removeItem(at: directory) }
        // What paging itself does is `MobileRemoteSupport.page`'s own test; here
        // only the route's job is checked — before and limit reach the host.
        let page = try await call(service, "GET", "/m1/sessions/session-1/entries?before=entry-6&limit=3")
        let ids = (page.1["entries"] as? [[String: Any]])?.compactMap { $0["id"] as? String }
        #expect(page.0 == 200 && ids == ["entry-3", "entry-4", "entry-5"] && page.1["hasMore"] as? Bool == true)
        #expect(host.recorded() == ["entries:entry-6:3"])
        #expect(try await call(service, "GET", "/m1/sessions/session-1/entries").0 == 400)
        #expect(try await call(service, "GET", "/m1/sessions/session-1/entries?before=entry-6&limit=0").0 == 400)
        #expect(try await call(service, "GET", "/m1/sessions/session-1/entries?before=entry-6&limit=101").0 == 400)
        #expect(try await call(service, "GET", "/m1/sessions/session-1/entries?before=entry-6&after=x").0 == 400)
        #expect(try await call(service, "GET", "/m1/sessions/session-1/entries?before=../etc").0 == 400)
        #expect(try await call(service, "GET", "/m1/sessions/missing/entries?before=entry-6").0 == 404)
    }

    @Test func settingsCheckTheBodyBeforeTheStateAndFollowTheViewMode() async throws {
        let host = FakeMobileHost()
        host.running = false
        let (service, directory) = await service(host)
        defer { try? FileManager.default.removeItem(at: directory) }
        // A plain pane offers only the CLI style, so a guided one is outside its options.
        #expect(try await call(service, "POST", "/m1/sessions/session-1/settings", body: ["mightyStyle": "ouroboros"]).0 == 400)
        // One POST may turn Mighty on and pick a style: the style is judged
        // against the view the same request asks for, not the one it replaces.
        #expect(try await call(service, "POST", "/m1/sessions/session-1/settings", body: ["agentViewMode": "mighty", "mightyStyle": "ouroboros"]).0 == 200)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/settings", body: ["mightyStyle": "paperthin"]).0 == 200)
        // Going back to plain takes the guided styles with it.
        #expect(try await call(service, "POST", "/m1/sessions/session-1/settings", body: ["agentViewMode": "plain", "mightyStyle": "ouroboros"]).0 == 400)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/settings", body: ["model": "gpt-5"]).0 == 400)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/settings", body: ["agentViewMode": "graph"]).0 == 400)
        #expect(try await call(service, "POST", "/m1/sessions/missing/settings", body: ["model": "opus"]).0 == 404)
        host.running = true
        // Shape and vocabulary before state: an empty body is a 400 even on a
        // pane whose state would refuse every change anyway.
        #expect(try await call(service, "POST", "/m1/sessions/session-1/settings", body: [:]).0 == 400)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/settings", body: ["model": "opus"]).0 == 409)
        // Only the two accepted bodies changed anything.
        #expect(host.recorded() == ["settings:-:mighty:ouroboros", "settings:-:-:paperthin"])
    }

    @Test func commandsListTheAppActionsAndOnlyThreeCanBePerformed() async throws {
        let host = FakeMobileHost()
        let (service, directory) = await service(host)
        defer { try? FileManager.default.removeItem(at: directory) }
        let listed = try await call(service, "GET", "/m1/sessions/session-1/commands")
        let commands = listed.1["commands"] as? [[String: Any]] ?? []
        let names = commands.compactMap { $0["name"] as? String }
        #expect(listed.0 == 200 && names.contains("model") && names.contains("clear") && names.contains("rename"))
        // Commands that only open a Mac window are not listed.
        #expect(!names.contains("plugin") && !names.contains("config"))
        #expect(commands.first { $0["name"] as? String == "clear" }?["action"] as? String == "clear")
        #expect(commands.first { $0["name"] as? String == "model" }?["action"] as? String == "model")
        #expect(commands.allSatisfy { ["app", "builtin", "project", "user", "plugin"].contains($0["source"] as? String ?? "") })
        #expect(try await call(service, "GET", "/m1/sessions/missing/commands").0 == 404)

        let help = try await call(service, "POST", "/m1/sessions/session-1/command", body: ["action": "help"])
        #expect(help.0 == 200 && help.1["ok"] as? Bool == true && help.1["message"] as? String == "본문 help")
        let clear = try await call(service, "POST", "/m1/sessions/session-1/command", body: ["action": "clear"])
        #expect(clear.0 == 200 && clear.1["message"] == nil)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/command", body: ["action": "rename"]).0 == 400)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/command", body: ["action": "model"]).0 == 400)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/command", body: [:]).0 == 400)
        #expect(host.recorded() == ["command:help", "command:clear"])
    }

    @Test func aLocalShellPaneIsRefusedWithAConflict() async throws {
        let host = FakeMobileHost()
        let (service, directory) = await service(host)
        defer { try? FileManager.default.removeItem(at: directory) }
        #expect(try await call(service, "POST", "/m1/workspaces/workspace-1/sessions", body: ["kind": "shell"]).0 == 409)
        #expect(try await call(service, "POST", "/m1/workspaces/workspace-1/sessions", body: ["kind": "claude"]).0 == 201)
    }

    /// Opens an upload, sends its one chunk and finishes it, as a phone does.
    private func upload(_ service: MobileRemoteService, device: String, name: String = "note.txt", bytes: Data = Data("문서".utf8)) async throws -> String {
        let opened = try await call(service, "POST", "/m1/sessions/session-1/uploads", body: ["name": name, "size": bytes.count], device: device)
        #expect(opened.0 == 201)
        let uploadId = try #require(opened.1["uploadId"] as? String)
        let chunk = try await call(service, "POST", "/m1/uploads/\(uploadId)/chunks/0", body: ["dataBase64": bytes.base64EncodedString()], device: device)
        #expect(chunk.0 == 200)
        #expect(try await call(service, "POST", "/m1/uploads/\(uploadId)/complete", device: device).0 == 200)
        return uploadId
    }

    @Test func onePhoneCannotTouchAnotherPhonesUpload() async throws {
        let host = FakeMobileHost()
        host.running = false
        let (service, directory) = await service(host)
        defer { try? FileManager.default.removeItem(at: directory) }
        let opened = try await call(service, "POST", "/m1/sessions/session-1/uploads", body: ["name": "note.txt", "size": 4], device: Self.phone)
        let uploadId = try #require(opened.1["uploadId"] as? String)
        // Not 403: whether the id exists is not the other phone's business.
        let bytes = Data(repeating: 65, count: 4)
        #expect(try await call(service, "POST", "/m1/uploads/\(uploadId)/chunks/0", body: ["dataBase64": bytes.base64EncodedString()], device: Self.other).0 == 404)
        #expect(try await call(service, "POST", "/m1/uploads/\(uploadId)/complete", device: Self.other).0 == 404)
        #expect(try await call(service, "POST", "/m1/uploads/\(uploadId)/cancel", device: Self.other).0 == 404)
        // The owner still has all of it, and only the owner may attach it.
        #expect(try await call(service, "POST", "/m1/uploads/\(uploadId)/chunks/0", body: ["dataBase64": bytes.base64EncodedString()], device: Self.phone).0 == 200)
        #expect(try await call(service, "POST", "/m1/uploads/\(uploadId)/complete", device: Self.phone).0 == 200)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "x", "attachments": [uploadId]], device: Self.other).0 == 400)
        #expect(try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "x", "attachments": [uploadId]], device: Self.phone).0 == 202)
        await service.shutdown()
    }

    @Test func twoSubmitsNamingOneUploadCannotBothSpendIt() async throws {
        let host = FakeMobileHost()
        host.running = false
        host.submitDelayMilliseconds = 250
        let (service, directory) = await service(host)
        defer { try? FileManager.default.removeItem(at: directory) }
        let uploadId = try await upload(service, device: Self.phone)
        // Both requests are inside the pane at the same time; exactly one of
        // them may be carrying the file.
        async let first = call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "하나", "attachments": [uploadId]])
        async let second = call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "둘", "attachments": [uploadId]])
        let statuses = [try await first.0, try await second.0].sorted()
        #expect(statuses == [202, 400])
        #expect(host.recorded().filter { $0.hasSuffix(":note.txt") }.count == 1)
        // Spent by the one that won: nobody can name it again.
        #expect(try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "셋", "attachments": [uploadId]]).0 == 400)
        await service.shutdown()
    }

    @Test func aRefusedSubmitHandsTheUploadBackToThePhone() async throws {
        let host = FakeMobileHost()
        host.running = false
        let (service, directory) = await service(host)
        defer { try? FileManager.default.removeItem(at: directory) }
        let uploadId = try await upload(service, device: Self.phone)
        host.failSubmit = true
        #expect(try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "보내줘", "attachments": [uploadId]]).0 == 409)
        // The claim went back with the refusal, so sending again just works.
        host.failSubmit = false
        let retried = try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "다시", "attachments": [uploadId]])
        #expect(retried.0 == 202 && retried.1["accepted"] as? String == "started")
        #expect(host.recorded() == ["submit:session-1:다시:note.txt"])
        await service.shutdown()
    }

    @Test func onlyTwoSubmitsHoldTheirAttachmentsAtOnce() async throws {
        let host = FakeMobileHost()
        host.running = false
        host.submitDelayMilliseconds = 200
        let (service, directory) = await service(host)
        defer { try? FileManager.default.removeItem(at: directory) }
        var ids: [String] = []
        for index in 0..<4 { ids.append(try await upload(service, device: Self.phone, name: "f\(index).txt")) }
        await withTaskGroup(of: Int.self) { group in
            for id in ids {
                group.addTask { (try? await self.call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "보내줘", "attachments": [id]]).0) ?? 0 }
            }
            for await status in group { #expect(status == 202) }
        }
        // Eight phones times eight 5 MB files would otherwise be hundreds of
        // megabytes of base64 alive at one moment.
        #expect(host.peakSubmits <= MobileRemoteService.concurrentAttachmentSubmits)
        #expect(host.recorded().count == 4)
        // A submit with no files does not queue behind them.
        host.submitDelayMilliseconds = 0
        #expect(try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "글만"]).0 == 202)
        await service.shutdown()
    }

    @Test func revokingRotatesTheKeyBeforeTheRowAndFailsWholeOrNotAtAll() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mobile-remote-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let clientId = "cGhvbmUtb25lLTAwMDAwMDA"
        let seed = MobileDeviceRegistry(url: directory.appendingPathComponent("devices.json"))
        guard case .issued = seed.issueToken(clientId: clientId, name: "iPhone") else { Issue.record("토큰을 발급하지 않았습니다."); return }

        let host = FakeMobileHost()
        host.running = false
        let service = MobileRemoteService(dataDirectory: directory, hostName: "Test Mac")
        await service.attach(host)
        let key = try await service.loadOrCreateKey()
        let uploadId = try await upload(service, device: clientId)

        // The key is rotated first, so a rotation that fails leaves the phone
        // paired: half a revoke must never be reported as a whole one.
        await service.setKeyRotationFailure("연결 키를 저장하지 못했습니다.")
        await #expect(throws: MightyError.self) { _ = try await service.revokeDevice(clientId) }
        let unchanged = await service.status()
        #expect(unchanged.devices.map(\.id) == [clientId])
        #expect(try await service.loadOrCreateKey() == key)
        // Its uploads are still its own, too.
        #expect(try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "아직", "attachments": [uploadId]], device: clientId).0 == 202)

        let second = try await upload(service, device: clientId)
        await service.setKeyRotationFailure(nil)
        let after = try await service.revokeDevice(clientId)
        #expect(after.devices.isEmpty)
        let rotated = try await service.loadOrCreateKey()
        #expect(rotated != key && after.key == nil)
        // Whatever the revoked phone was still holding went with it.
        #expect(try await call(service, "POST", "/m1/sessions/session-1/submit", body: ["text": "이제", "attachments": [second]], device: clientId).0 == 400)
        await #expect(throws: MightyError.self) { _ = try await service.revokeDevice(clientId) }
        await service.shutdown()
    }

    @Test func legacyPhonesAreAllowedUnlessTheSettingSaysOtherwise() throws {
        // Absent from an older saved file: allowed, so an upgrade breaks nothing.
        let old = try JSONDecoder().decode(MobileRemoteSettings.self, from: Data(#"{"enabled":true,"relayURL":"wss://relay.example.com"}"#.utf8))
        #expect(old.allowLegacyPhones && old.enabled)
        let off = try JSONDecoder().decode(MobileRemoteSettings.self, from: Data(#"{"allowLegacyPhones":false}"#.utf8))
        #expect(!off.allowLegacyPhones)
        // Normalising keeps the choice; only the relay address is rewritten.
        let normalized = MobileRemoteSettings(enabled: true, relayURL: "wss://relay.example.com/", allowLegacyPhones: false).normalized
        #expect(!normalized.allowLegacyPhones && normalized.relayURL == "wss://relay.example.com")
        #expect(MobileRemoteSettings() != MobileRemoteSettings(allowLegacyPhones: false))
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
        // Seed one device so regeneration has something to clear.
        let seededRegistry = MobileDeviceRegistry(url: directory.appendingPathComponent("devices.json"))
        guard case .issued = seededRegistry.issueToken(clientId: "cGhvbmUtb25lLTAwMDAwMDA", name: "iPhone") else {
            Issue.record("토큰을 발급하지 못했습니다."); return
        }
        // Confirm the seed worked via the registry that wrote it.
        #expect(seededRegistry.all().count == 1)
        let rotated = try await service.regenerateKey()
        let afterRotation = try await service.loadOrCreateKey()
        #expect(rotated != key && afterRotation == rotated)
        // A new key means every phone must re-pair: the device list is empty.
        #expect(await service.status().devices.isEmpty)

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

    // MARK: - Mighty-default AC

    @Test func sendsMightyIsTrueForEveryNonShellKindRegardlessOfViewMode() {
        // Non-shell panes always carry a mighty payload so the phone can open
        // the blocks view by default, whatever view mode the Mac pane itself is in.
        for kind in ["claude", "codex", "browser"] {
            #expect(MobileRemoteSupport.sendsMighty(kind: kind, agentViewMode: nil),
                    "expected mighty for kind=\(kind) viewMode=nil")
            #expect(MobileRemoteSupport.sendsMighty(kind: kind, agentViewMode: MobileWire.plainViewMode),
                    "expected mighty for kind=\(kind) viewMode=plain")
            #expect(MobileRemoteSupport.sendsMighty(kind: kind, agentViewMode: "mighty"),
                    "expected mighty for kind=\(kind) viewMode=mighty")
        }
    }

    @Test func sendsMightyIsFalseForShellRegardlessOfViewMode() {
        #expect(!MobileRemoteSupport.sendsMighty(kind: "shell", agentViewMode: nil))
        #expect(!MobileRemoteSupport.sendsMighty(kind: "shell", agentViewMode: MobileWire.plainViewMode))
        #expect(!MobileRemoteSupport.sendsMighty(kind: "shell", agentViewMode: "mighty"))
    }
}
