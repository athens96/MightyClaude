import Testing
import Foundation
@testable import MightyCore

/// The remote mirror (docs/remote-workspaces.md "그 컴퓨터의 창 그대로"): the shared wire samples, the bounds and revisions
/// both sides keep, what is never saved, and the host's routes over loopback.
struct RemoteMirrorWireTests {
    static let folder: URL = {
        var directory = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        while !FileManager.default.fileExists(atPath: directory.appendingPathComponent("native/contracts/fixtures/remote-mirror/endpoints.json").path) {
            directory.deleteLastPathComponent()
        }
        return directory.appendingPathComponent("native/contracts/fixtures/remote-mirror")
    }()
    static func data(_ name: String) throws -> Data { try Data(contentsOf: folder.appendingPathComponent(name)) }

    @Test func theIndexNamesEveryRouteAndEveryFileIsAccountedFor() throws {
        let index = try #require(JSONSerialization.jsonObject(with: Self.data("endpoints.json")) as? [[String: Any]])
        #expect(Set(index.compactMap { $0["name"] as? String }) == ["list", "detail", "submit", "stop", "permission", "questions", "plan", "close", "create"])
        var named: Set<String> = ["endpoints.json", "state-unchanged.json"]
        for entry in index {
            #expect((entry["path"] as? String)?.hasPrefix(RemoteMirrorRoutes.prefix) == true)
            for key in ["request", "response"] { if let name = entry[key] as? String { named.insert(name) } }
        }
        let files = Set(try FileManager.default.contentsOfDirectory(atPath: Self.folder.path))
        #expect(files == named, "unaccounted: \(files.symmetricDifference(named).sorted())")
    }

    @Test func theSamplesDecodeValidateAndRoundTrip() throws {
        let state = try JSONDecoder().decode(RemoteWireMirrorState.self, from: Self.data("state.json"))
        #expect(RemoteMirrorSupport.valid(state))
        #expect(state.hostName == "studio-mac" && state.workspaces.map(\.id) == ["ws-project"] && state.sessions.map(\.id) == ["pane-1", "pane-2"])
        #expect(state.sessions[0].pending == 1 && state.sessions[0].queued == 1 && state.sessions[0].branch == "mighty/pane-1" && state.sessions[1].branch == nil)
        let unchanged = try JSONDecoder().decode(RemoteWireMirrorState.self, from: Self.data("state-unchanged.json"))
        #expect(RemoteMirrorSupport.valid(unchanged) && unchanged.unchanged == true && unchanged.sessions.isEmpty)
        let detail = try JSONDecoder().decode(RemoteWireMirrorDetail.self, from: Self.data("detail.json"))
        #expect(RemoteMirrorSupport.valid(detail, sessionId: "pane-1"))
        #expect(!RemoteMirrorSupport.valid(detail, sessionId: "pane-2"), "a detail answers for the pane asked about only")
        #expect(detail.session.logs.count == 2 && detail.session.graphRuns.count == 1 && detail.session.permissions.first?.toolName == "Write")
        #expect(detail.session.queued == [RemoteMirrorQueued(id: "queued-1", text: "Then update CHANGELOG.md")] && detail.session.base == "main")
        #expect(detail.session.settings.permissionMode == "acceptEdits" && detail.session.todoProgress?.items.count == 1)
        let submit = try JSONDecoder().decode(RemoteWireMirrorSubmit.self, from: Self.data("submit.json"))
        #expect(submit.text == "Then update CHANGELOG.md" && submit.attachments.count == 1)
        #expect(try JSONDecoder().decode(RemoteWireMirrorOutcome.self, from: Self.data("outcome.json")).outcome == "queued")
        let permission = try JSONDecoder().decode(RemoteWireMirrorPermission.self, from: Self.data("permission.json"))
        #expect(permission.requestId == "perm-4" && permission.allow && permission.always == false)
        #expect(try JSONDecoder().decode(RemoteWireMirrorQuestions.self, from: Self.data("questions.json")).answers["Which file?"]?.selectedOptions == ["README.md"])
        #expect(try JSONDecoder().decode(RemoteWirePlan.self, from: Self.data("plan.json")).decision != nil)
        #expect(try JSONDecoder().decode(RemoteWireMirrorCreate.self, from: Self.data("create.json")).provider == "claude")
        #expect(try JSONDecoder().decode(RemoteWireMirrorCreated.self, from: Self.data("created.json")).sessionId == "pane-3")
        #expect(try JSONDecoder().decode(RemoteWireAnswer.self, from: Self.data("answer.json")).accepted)
        // What the Mac writes reads back the same.
        let again = try JSONDecoder().decode(RemoteWireMirrorDetail.self, from: JSONEncoder().encode(detail))
        #expect(again.session == detail.session && again.revision == detail.revision)
        let listed = try JSONDecoder().decode(RemoteWireMirrorState.self, from: JSONEncoder().encode(state))
        #expect(listed.sessions == state.sessions && listed.workspaces == state.workspaces)
    }

    @Test func aClientRefusesListsOutsideTheRules() throws {
        let state = try JSONDecoder().decode(RemoteWireMirrorState.self, from: Self.data("state.json"))
        var other = state; other.mirror = 2
        #expect(!RemoteMirrorSupport.valid(other))
        var status = state; status.sessions[0].status = "thinking"
        #expect(!RemoteMirrorSupport.valid(status))
        var unlisted = state; unlisted.sessions[0].workspaceId = "ws-elsewhere"
        #expect(!RemoteMirrorSupport.valid(unlisted))
        var twice = state; twice.sessions[1].id = "pane-1"
        #expect(!RemoteMirrorSupport.valid(twice))
        var shell = state; shell.sessions[0].kind = "shell"
        #expect(!RemoteMirrorSupport.valid(shell))
        var badId = state; badId.sessions[0].id = "../pane"
        #expect(!RemoteMirrorSupport.valid(badId))
        var many = state; many.sessions = (0...RemoteMirrorProtocol.maximumSessions).map { var pane = state.sessions[1]; pane.id = "pane-x\($0)"; return pane }
        #expect(!RemoteMirrorSupport.valid(many))
        let detail = try JSONDecoder().decode(RemoteWireMirrorDetail.self, from: Self.data("detail.json"))
        var long = detail; long.session.logs = (0...RemoteMirrorProtocol.maximumEntries).map { LogEntry(id: "log-\($0)", kind: "user", text: "x") }
        #expect(!RemoteMirrorSupport.valid(long, sessionId: "pane-1"))
        var kind = detail; kind.session.logs[0].kind = "secret"
        #expect(!RemoteMirrorSupport.valid(kind, sessionId: "pane-1"))
    }
}

struct RemoteMirrorSupportTests {
    @Test func clientIdsAreStablePerConnectionAndValidIdentifiers() {
        let id = RemoteMirrorSupport.clientId(connectionId: "conn-1", hostId: "pane-1")
        #expect(id == RemoteMirrorSupport.clientId(connectionId: "conn-1", hostId: "pane-1"))
        #expect(id != RemoteMirrorSupport.clientId(connectionId: "conn-2", hostId: "pane-1"))
        #expect(id != RemoteMirrorSupport.clientId(connectionId: "conn-1", hostId: "pane-2"))
        // The separator keeps "a"+"bc" and "ab"+"c" apart.
        #expect(RemoteMirrorSupport.clientId(connectionId: "a", hostId: "bc") != RemoteMirrorSupport.clientId(connectionId: "ab", hostId: "c"))
        #expect(id.hasPrefix("mirror-") && id.count == 7 + 32 && CoreValidation.identifier(id))
    }

    @Test func aPanesRevisionMovesWithWhatItsDetailCarries() {
        var session = RunSession(id: "pane-1", workspaceId: "ws", title: "Pane")
        session.logs = [LogEntry(id: "log-1", kind: "user", text: "Hi")]
        let base = RemoteMirrorSupport.revision(of: session, permissions: [], queued: [], branch: nil)
        #expect(base == RemoteMirrorSupport.revision(of: session, permissions: [], queued: [], branch: nil))
        var running = session; running.status = "running"
        #expect(RemoteMirrorSupport.revision(of: running, permissions: [], queued: [], branch: nil) != base)
        var streamed = session; streamed.logs[0].text += " there"
        #expect(RemoteMirrorSupport.revision(of: streamed, permissions: [], queued: [], branch: nil) != base, "a growing entry moves it")
        var added = session; added.logs.append(LogEntry(id: "log-2", kind: "assistant", text: "Hello"))
        #expect(RemoteMirrorSupport.revision(of: added, permissions: [], queued: [], branch: nil) != base)
        var mode = session; mode.settings.permissionMode = "plan"
        #expect(RemoteMirrorSupport.revision(of: mode, permissions: [], queued: [], branch: nil) != base)
        let request = ToolPermissionRequest(id: "perm-1", runId: "run-1", toolUseId: "toolu", toolName: "Bash", inputJSON: "{}", summary: "ls")
        #expect(RemoteMirrorSupport.revision(of: session, permissions: [request], queued: [], branch: nil) != base)
        #expect(RemoteMirrorSupport.revision(of: session, permissions: [], queued: [QueuedInput(text: "Next")], branch: nil) != base)
        #expect(RemoteMirrorSupport.revision(of: session, permissions: [], queued: [], branch: "mighty/x") != base)
        #expect(base.count == 24 && base.allSatisfy(\.isHexDigit))
    }

    @Test func theDetailIsBoundedLikeThePhonesAndLeavesHostPathsOut() throws {
        var session = RunSession(id: "pane-1", workspaceId: "ws", title: String(repeating: "t", count: 300))
        session.logs = (0..<200).map { LogEntry(id: "log-\($0)", kind: "assistant", text: "entry \($0)") }
        session.logs.append(LogEntry(id: "picture", kind: "image", text: "a picture", images: [AgentImageRef(hash: String(repeating: "a", count: 64), mediaType: "image/png", width: 1, height: 1, bytes: 1, source: "tool")]))
        session.graphRuns = (0..<30).map { index in
            var run = MightyGraphRun(id: "run-\(index)", input: "request \(index)")
            run.agents = [MightyGraphAgent(id: "agent-\(index)", title: "Scout", input: "look")]
            run.agents[0].outputFile = "/Users/host/tmp/output-\(index).txt"
            return run
        }
        let answered = ToolPermissionRequest(id: "perm-0", runId: "run-1", toolUseId: "toolu", toolName: "Bash", inputJSON: "{}", summary: "ls", state: "allowed")
        let pending = ToolPermissionRequest(id: "perm-1", runId: "run-1", toolUseId: "toolu", toolName: "Bash", inputJSON: "{}", summary: "ls")
        let queued = (0..<20).map { QueuedInput(text: String(repeating: "q", count: 900) + "\($0)") }
        let detail = RemoteMirrorSupport.session(session, permissions: [answered, pending], queued: queued, branch: "mighty/p", base: "main")
        #expect(detail.logs.count == RemoteMirrorProtocol.maximumEntries && detail.hasOlder && detail.logs.last?.id == "picture")
        #expect(detail.logs.allSatisfy { $0.images == nil && $0.kind != "image" }, "pictures are the host's cache keys")
        #expect(detail.graphRuns.count == RemoteMirrorProtocol.maximumGraphRuns && detail.graphRuns.last?.id == "run-29")
        #expect(detail.graphRuns.allSatisfy { $0.agents.allSatisfy { $0.outputFile == nil } }, "task output files are host paths")
        #expect(detail.permissions.map(\.id) == ["perm-1"])
        #expect(detail.queued.count == RemoteMirrorProtocol.maximumQueued && detail.queued.allSatisfy { $0.text.count == RemoteMirrorProtocol.maximumQueuedText })
        #expect(detail.title.count == 200 && detail.branch == "mighty/p" && detail.base == "main")
        #expect(try JSONEncoder().encode(detail).count <= RemoteMirrorProtocol.maximumDetailBytes)
    }

    @Test func anOversizeDetailIsCutUnderTheBudget() throws {
        var session = RunSession(id: "pane-1", workspaceId: "ws", title: "Big")
        session.logs = (0..<40).map { LogEntry(id: "log-\($0)", kind: "assistant", text: String(repeating: "x", count: 100_000)) }
        let detail = RemoteMirrorSupport.session(session, permissions: [], queued: [])
        #expect(try JSONEncoder().encode(detail).count <= RemoteMirrorProtocol.maximumDetailBytes)
        #expect(detail.hasOlder && detail.logs.last?.id == "log-39", "the newest entries stay")
        // One entry alone over the budget is cut to 32 KiB.
        var huge = RunSession(id: "pane-2", workspaceId: "ws", title: "Huge")
        huge.logs = [LogEntry(id: "log-1", kind: "assistant", text: String(repeating: "y", count: 3_000_000))]
        let cut = RemoteMirrorSupport.session(huge, permissions: [], queued: [])
        #expect(cut.logs.count == 1 && cut.logs[0].text.utf8.count == 32 * 1024)
    }

    @Test func aClientPaneTakesTheHostsFieldsAndKeepsItsOwn() throws {
        let summary = RemoteMirrorSummary(id: "pane-1", workspaceId: "ws-host", title: "Host pane", kind: "claude", provider: "codex", model: "gpt-5", status: "running", revision: "ab")
        let clientId = RemoteMirrorSupport.clientId(connectionId: "conn", hostId: "pane-1")
        var pane = RemoteMirrorSupport.pane(from: summary, existing: nil, clientId: clientId, workspaceId: "mirror-ws")
        #expect(pane.id == clientId && pane.workspaceId == "mirror-ws" && pane.title == "Host pane" && pane.provider == "codex" && pane.model == "gpt-5" && pane.status == "running")
        pane.mirror = RemoteMirrorReference(connectionId: "conn", hostSessionId: "pane-1", hostWorkspaceId: "ws-host", hostName: "Studio")
        pane.agentViewMode = "timeline"
        let detail = try JSONDecoder().decode(RemoteWireMirrorDetail.self, from: RemoteMirrorWireTests.data("detail.json")).session
        let applied = RemoteMirrorSupport.apply(detail, to: pane)
        #expect(applied.id == clientId && applied.workspaceId == "mirror-ws" && applied.mirror == pane.mirror)
        #expect(applied.logs == detail.logs && applied.settings == detail.settings && applied.graphRuns?.map(\.id) == ["gr-1"] && applied.todoProgress == detail.todoProgress)
        #expect(applied.agentViewMode == "timeline", "a view chosen here stays when the host sends none")
        let started = try #require(applied.runTiming)
        #expect(started.startedAt == ISO8601DateFormatter().date(from: "2026-11-13T01:00:01Z") && started.finishedAt == nil)
        var idle = detail; idle.startedAt = nil
        #expect(RemoteMirrorSupport.apply(idle, to: applied).runTiming == nil)
    }

    @Test func mirroredPanesAndWorkspacesAreNeverSaved() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-mirror-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appendingPathComponent("project")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let repository = StateRepository(directory: root.appendingPathComponent("state"), legacyStateURL: nil)
        let local = try await repository.approveWorkspace(Workspace(name: "Project", path: folder.path))
        let mirrorWorkspace = Workspace(id: RemoteMirrorSupport.clientId(connectionId: "conn", hostId: "ws-host"), name: "Host project", path: "/Users/host/project",
                                        remote: RemoteWorkspaceReference(connectionId: "conn", workspaceId: "ws-host", hostName: "Studio", mirror: true))
        let mine = RunSession(id: "pane-local", workspaceId: local.id, title: "Mine")
        var mirrored = RunSession(id: RemoteMirrorSupport.clientId(connectionId: "conn", hostId: "pane-1"), workspaceId: mirrorWorkspace.id, title: "Theirs")
        mirrored.mirror = RemoteMirrorReference(connectionId: "conn", hostSessionId: "pane-1", hostWorkspaceId: "ws-host", hostName: "Studio")
        mirrored.logs = [LogEntry(kind: "assistant", text: "the host's transcript")]
        var snapshot = AppSnapshot(workspaces: [local, mirrorWorkspace], sessions: [mine, mirrored])
        snapshot.activeWorkspaceId = mirrorWorkspace.id
        snapshot.activeSessionId = mirrored.id
        snapshot.paneLayouts = [mirrorWorkspace.id: PaneLayoutNode(kind: "group", sessionIds: [mirrored.id])]
        snapshot.expandedWorkspaceIds = [local.id, mirrorWorkspace.id]
        let stripped = snapshot.withoutMirrors()
        #expect(stripped.workspaces.map(\.id) == [local.id] && stripped.sessions.map(\.id) == ["pane-local"])
        #expect(stripped.activeWorkspaceId == local.id && stripped.activeSessionId == nil)
        #expect(stripped.paneLayouts?[mirrorWorkspace.id] == nil && stripped.expandedWorkspaceIds == [local.id])
        // The repository saves the snapshot as it is (the mirror's workspace was never approved here) and writes none of it.
        try await repository.save(snapshot)
        let saved = try String(contentsOf: root.appendingPathComponent("state/workspace-state.json"), encoding: .utf8)
        #expect(!saved.contains("the host's transcript") && !saved.contains(mirrorWorkspace.id) && !saved.contains("\"mirror\""))
        // A pane's link to the host never reaches JSON, even on its own.
        let encoded = try #require(String(data: JSONEncoder().encode(mirrored), encoding: .utf8))
        #expect(!encoded.contains("hostSessionId"))
    }

    @Test func onlyTheSubmitRouteTakesAttachmentSizedBodies() {
        #expect(RemoteMirrorRoutes.isSubmit("/v2/mirror/sessions/pane-1/submit"))
        #expect(!RemoteMirrorRoutes.isSubmit("/v2/mirror/sessions/pane-1/stop"))
        #expect(!RemoteMirrorRoutes.isSubmit("/v2/mirror/sessions/../submit"))
        #expect(!RemoteMirrorRoutes.isSubmit("/v2/mirror/sessions/pane-1/submit/x"))
    }
}

/// The host's half, as a fake app: records what the routes ask of it.
private final class FakeMirrorApp: RemoteMirrorHosting, @unchecked Sendable {
    private let lock = NSLock()
    private var sessions: [RunSession]
    private var calls: [String] = []
    init(_ sessions: [RunSession]) { self.sessions = sessions }
    var recorded: [String] { lock.withLock { calls } }
    private func record(_ call: String) { lock.withLock { calls.append(call) } }
    func setStatus(_ id: String, _ status: String) { lock.withLock { if let index = sessions.firstIndex(where: { $0.id == id }) { sessions[index].status = status } } }

    func mirrorSummaries(workspaceIds: Set<String>) async -> [RemoteMirrorSummary] {
        lock.withLock {
            sessions.filter { workspaceIds.contains($0.workspaceId) }.map {
                RemoteMirrorSummary(id: $0.id, workspaceId: $0.workspaceId, title: $0.title, kind: $0.kind, provider: $0.provider, model: $0.model, status: $0.status,
                                    revision: RemoteMirrorSupport.revision(of: $0, permissions: [], queued: [], branch: nil))
            }
        }
    }
    func mirrorDetail(sessionId: String, workspaceIds: Set<String>) async -> RemoteMirrorPane? {
        lock.withLock {
            sessions.first { $0.id == sessionId && workspaceIds.contains($0.workspaceId) }.map {
                RemoteMirrorPane(revision: RemoteMirrorSupport.revision(of: $0, permissions: [], queued: [], branch: nil), session: RemoteMirrorSupport.session($0, permissions: [], queued: []))
            }
        }
    }
    func mirrorPaneInfo(sessionId: String) async -> RemoteMirrorPaneInfo? {
        lock.withLock { sessions.first { $0.id == sessionId }.map { RemoteMirrorPaneInfo(workspaceId: $0.workspaceId, permissionMode: $0.settings.permissionMode) } }
    }
    func mirrorSubmit(sessionId: String, text: String, attachments: [RunAttachment]) async throws -> String { record("submit \(sessionId) \(text) \(attachments.count)"); return "queued" }
    func mirrorStop(sessionId: String) async throws { record("stop \(sessionId)") }
    func mirrorPermission(sessionId: String, requestId: String, runId: String, allow: Bool, always: Bool) async throws {
        record("permission \(sessionId) \(requestId) \(runId) \(allow) \(always)")
    }
    func mirrorAnswers(sessionId: String, requestId: String, runId: String, answers: [String: UserQuestionAnswer]) async throws {
        record("answers \(sessionId) \(requestId) \(answers.keys.sorted())")
    }
    func mirrorPlan(sessionId: String, requestId: String, runId: String, decision: PlanDecision) async throws { record("plan \(sessionId) \(requestId)") }
    func mirrorCreateSession(workspaceId: String, provider: String) async throws -> String {
        if provider == "codex" { throw MightyError("Codex is not installed here") }
        record("create \(workspaceId) \(provider)"); return "pane-new"
    }
    func mirrorClose(sessionId: String) async throws { record("close \(sessionId)") }
}

@Suite(.serialized) struct RemoteMirrorLoopbackTests {
    private struct Host {
        let root: URL
        let shared: Workspace
        let hidden: Workspace
        let providers: ProviderService
        let service: RemoteHostService
        let app: FakeMirrorApp
        func cleanup() async { _ = await service.stop(); await providers.shutdown(); try? FileManager.default.removeItem(at: root) }
    }

    private func host() async throws -> Host {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-mirror-\(UUID().uuidString)")
        for name in ["project", "private", "plugin/.claude-plugin"] {
            try FileManager.default.createDirectory(at: root.appendingPathComponent(name), withIntermediateDirectories: true)
        }
        try Data("{}".utf8).write(to: root.appendingPathComponent("plugin/.claude-plugin/plugin.json"))
        let repository = StateRepository(directory: root.appendingPathComponent("host"), legacyStateURL: nil)
        let shared = try await repository.approveWorkspace(Workspace(name: "Project", path: root.appendingPathComponent("project").path))
        let hidden = try await repository.approveWorkspace(Workspace(name: "Private", path: root.appendingPathComponent("private").path))
        try await repository.save(AppSnapshot(workspaces: [shared, hidden]))
        var full = RunSession(id: "pane-full", workspaceId: shared.id, title: "Full access")
        full.settings.permissionMode = "fullAccess"
        let app = FakeMirrorApp([RunSession(id: "pane-a", workspaceId: shared.id, title: "Shared pane"), full,
                                 RunSession(id: "pane-b", workspaceId: hidden.id, title: "Private pane")])
        let providers = ProviderService()
        let service = RemoteHostService(repository: repository, providers: providers, pluginDirectory: root.appendingPathComponent("plugin"), appVersion: "9.9.9",
                                        hostName: "Test host", allowLoopbackForTests: true, heartbeat: RemoteProtocol.heartbeatTimeout, admission: { _ in nil })
        return Host(root: root, shared: shared, hidden: hidden, providers: providers, service: service, app: app)
    }

    private func status(_ operation: () async throws -> Void) async -> Int? {
        do { try await operation(); return nil } catch { return (error as? RemoteHTTPFailure)?.status }
    }

    @Test func theHostShowsAndDrivesOnlyItsSharedWorkspacesPanes() async throws {
        let host = try await host()
        do {
            let state = try await host.service.start(workspaceIds: [host.shared.id], port: 0)
            let key = try #require(state.key), port = try #require(state.port)
            let target = RemoteTarget(address: RemoteAddress(host: "127.0.0.1", port: port), ip: "127.0.0.1", port: port)
            func get(_ path: String) async throws -> Data { try await RemoteTransport.request(target, key: key, method: "GET", path: path) }
            func post(_ path: String, _ body: String) async throws -> Data { try await RemoteTransport.request(target, key: key, method: "POST", path: path, body: Data(body.utf8)) }
            // Before the app hands the host its panes, there is no mirror (an older host answers the same).
            #expect(await status { _ = try await get("/v2/mirror") } == 404)
            await host.service.setMirror(host.app)
            #expect(await status { _ = try await RemoteTransport.request(target, key: String(repeating: "Z", count: 43), method: "GET", path: "/v2/mirror") } == 401)

            let list = try JSONDecoder().decode(RemoteWireMirrorState.self, from: try await get("/v2/mirror"))
            #expect(RemoteMirrorSupport.valid(list) && list.hostName == "Test host" && list.unchanged == nil)
            #expect(list.workspaces.map(\.id) == [host.shared.id] && list.sessions.map(\.id) == ["pane-a", "pane-full"])
            // The same revision again: nothing else is sent.
            let same = try JSONDecoder().decode(RemoteWireMirrorState.self, from: try await get("/v2/mirror?since=\(list.revision)"))
            #expect(same.unchanged == true && same.sessions.isEmpty && same.workspaces.isEmpty && same.revision == list.revision)
            host.app.setStatus("pane-a", "running")
            let moved = try JSONDecoder().decode(RemoteWireMirrorState.self, from: try await get("/v2/mirror?since=\(list.revision)"))
            #expect(moved.unchanged == nil && moved.revision != list.revision && moved.sessions.first?.status == "running")
            #expect(await status { _ = try await get("/v2/mirror?since=XYZ") } == 400)
            #expect(await status { _ = try await get("/v2/mirror?other=1") } == 400)

            let detail = try JSONDecoder().decode(RemoteWireMirrorDetail.self, from: try await get("/v2/mirror/sessions/pane-a"))
            #expect(RemoteMirrorSupport.valid(detail, sessionId: "pane-a") && detail.session.status == "running")
            // A pane of a workspace not shared is unknown, whatever is asked of it.
            #expect(await status { _ = try await get("/v2/mirror/sessions/pane-b") } == 404)
            #expect(await status { _ = try await post("/v2/mirror/sessions/pane-b/submit", #"{"text":"hi"}"#) } == 404)
            #expect(await status { _ = try await post("/v2/mirror/sessions/pane-b/stop", "{}") } == 404)
            #expect(await status { _ = try await get("/v2/mirror/sessions/nope") } == 404)

            let outcome = try JSONDecoder().decode(RemoteWireMirrorOutcome.self, from: try await post("/v2/mirror/sessions/pane-a/submit", String(decoding: RemoteMirrorWireTests.data("submit.json"), as: UTF8.self)))
            #expect(outcome.outcome == "queued")
            #expect(await status { _ = try await post("/v2/mirror/sessions/pane-a/submit", #"{"text":"   "}"#) } == 400)
            #expect(await status { _ = try await post("/v2/mirror/sessions/pane-a/submit", "not json") } == 400)
            // A full-access pane takes others' requests only with the host's switch on.
            #expect(await status { _ = try await post("/v2/mirror/sessions/pane-full/submit", #"{"text":"go"}"#) } == 403)
            #expect(await status { _ = try await post("/v2/mirror/sessions/pane-a/permission", #"{"requestId":"perm-1","runId":"run-1","allow":true,"always":true}"#) } == 403)
            _ = try await post("/v2/mirror/sessions/pane-a/permission", String(decoding: RemoteMirrorWireTests.data("permission.json"), as: UTF8.self))
            _ = await host.service.setAllowsFullAccess(true)
            _ = try await post("/v2/mirror/sessions/pane-full/submit", #"{"text":"go"}"#)
            _ = try await post("/v2/mirror/sessions/pane-a/permission", #"{"requestId":"perm-2","runId":"run-1","allow":true,"always":true}"#)
            _ = try await post("/v2/mirror/sessions/pane-a/questions", String(decoding: RemoteMirrorWireTests.data("questions.json"), as: UTF8.self))
            _ = try await post("/v2/mirror/sessions/pane-a/plan", String(decoding: RemoteMirrorWireTests.data("plan.json"), as: UTF8.self))
            _ = try await post("/v2/mirror/sessions/pane-a/stop", "{}")
            _ = try await post("/v2/mirror/sessions/pane-a/close", "{}")

            let created = try JSONDecoder().decode(RemoteWireMirrorCreated.self, from: try await post("/v2/mirror/workspaces/\(host.shared.id)/sessions", #"{"provider":"claude"}"#))
            #expect(created.sessionId == "pane-new")
            #expect(await status { _ = try await post("/v2/mirror/workspaces/\(host.hidden.id)/sessions", #"{"provider":"claude"}"#) } == 403)
            #expect(await status { _ = try await post("/v2/mirror/workspaces/\(host.shared.id)/sessions", #"{"provider":"nobody"}"#) } == 400)
            // The app's refusal comes back as 409 with its reason.
            #expect(await status { _ = try await post("/v2/mirror/workspaces/\(host.shared.id)/sessions", #"{"provider":"codex"}"#) } == 409)

            #expect(host.app.recorded == [
                "submit pane-a Then update CHANGELOG.md 1",
                "permission pane-a perm-4 job-1 true false",
                "submit pane-full go 0",
                "permission pane-a perm-2 run-1 true true",
                "answers pane-a perm-2 [\"Which file?\"]",
                "plan pane-a perm-3",
                "stop pane-a",
                "close pane-a",
                "create \(host.shared.id) claude",
            ])
            await host.cleanup()
        } catch { await host.cleanup(); throw error }
    }

    /// The client service over the same loopback: connect (which reads `/v2/info`, so the host probes its CLIs), the list,
    /// a detail, a send and a new pane, and a host that stopped sharing leaves the connection disconnected.
    @Test(.disabled(if: CIRunner.onGitHubActions, CIRunner.timing)) func theClientReadsAndDrivesTheMirror() async throws {
        let host = try await host()
        let client = RemoteClientService(repository: StateRepository(directory: host.root.appendingPathComponent("client"), legacyStateURL: nil),
                                         dataDirectory: host.root.appendingPathComponent("client"), allowLoopbackForTests: true) { _ in }
        do {
            await host.service.setMirror(host.app)
            _ = await host.providers.runtimeInfo(appVersion: "9.9.9")
            let state = try await host.service.start(workspaceIds: [host.shared.id], port: 0)
            let info = try await client.connect(name: "Studio", address: "127.0.0.1:\(try #require(state.port))", key: try #require(state.key))
            let list = try await client.mirrorState(connectionId: info.id, since: nil)
            #expect(!list.unchanged && list.hostName == "Test host" && list.sessions.map(\.id) == ["pane-a", "pane-full"])
            #expect(try await client.mirrorState(connectionId: info.id, since: list.revision).unchanged)
            let pane = try await client.mirrorDetail(connectionId: info.id, hostSessionId: "pane-a")
            #expect(pane.session.id == "pane-a" && pane.revision == list.sessions[0].revision)
            #expect(try await client.mirrorSubmit(connectionId: info.id, hostSessionId: "pane-a", text: "hello", attachments: []) == "queued")
            #expect(try await client.mirrorCreateSession(connectionId: info.id, hostWorkspaceId: host.shared.id, provider: "claude") == "pane-new")
            // An older host (or one without the mirror) refuses with 404: the connection stays.
            await #expect(throws: (any Error).self) { _ = try await client.mirrorDetail(connectionId: info.id, hostSessionId: "pane-b") }
            #expect(await client.list().first?.status == .connected)
            // Sharing again revokes the key: the next read leaves the connection disconnected.
            _ = await host.service.stop()
            _ = try await host.service.start(workspaceIds: [host.shared.id], port: state.port!)
            await #expect(throws: (any Error).self) { _ = try await client.mirrorState(connectionId: info.id, since: nil) }
            #expect(await client.list().first?.status == .disconnected)
            await client.shutdown(); await host.cleanup()
        } catch { await client.shutdown(); await host.cleanup(); throw error }
    }
}
