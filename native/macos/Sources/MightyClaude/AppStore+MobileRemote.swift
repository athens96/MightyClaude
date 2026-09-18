import AppKit
import Combine
import CoreImage
import MightyCore
import SwiftUI

/// Revision bookkeeping for the mobile protocol. A session's revision moves
/// whenever anything about it changed; the state revision moves only when a
/// summary (status, title, preview, pending counts) changed, so the phone's
/// list is not re-sent for every streamed chunk.
struct MobileRemoteTracking {
    var stateRevision = 1
    var sessionRevisions: [String: Int] = [:]
    var seen: [String: SessionFingerprint] = [:]
    var summaries: [String: MobileSessionSummary] = [:]
    var order: [String] = []
    var workspaces: [Workspace] = []

    /// Cheap change detector for a pane: metadata plus the tail of the
    /// transcript, so streaming chunks and tool-state updates are noticed
    /// without comparing every saved entry on each publish.
    struct SessionFingerprint: Equatable {
        var status: String; var title: String; var provider: String; var model: String; var resumeId: String?
        var count: Int; var tail: [String]; var usage: String?
        init(_ session: RunSession) {
            status = session.status; title = session.title; provider = session.provider; model = session.model; resumeId = session.resumeId
            count = session.logs.count
            tail = session.logs.suffix(12).map { "\($0.id):\($0.text.utf8.count):\($0.activity?.state ?? "")" }
            usage = session.sessionUsage?.updatedAt
        }
    }
}

/// Hops every protocol call onto the main actor where the store lives.
final class MobileRemoteBridge: MobileHostDelegate, @unchecked Sendable {
    private weak var store: AppStore?
    init(store: AppStore) { self.store = store }

    func mobileState() async -> MobileState {
        await MainActor.run { store?.mobileState() ?? MobileState(revision: 0, hostName: "", workspaces: [], sessions: []) }
    }
    func mobileSession(id: String) async -> MobileSessionDetail? { await MainActor.run { store?.mobileSessionDetail(id) } }
    func mobileSubmit(sessionId: String, text: String) async throws -> String {
        try await MainActor.run { try store.orClosing().mobileSubmit(sessionId, text: text) }
    }
    func mobileStop(sessionId: String) async throws {
        let store = try await MainActor.run { () -> AppStore in
            let store = try self.store.orClosing()
            try store.mobileValidateCommand(sessionId)
            return store
        }
        await store.stop(sessionId)
    }
    func mobilePermission(sessionId: String, requestId: String, runId: String, allow: Bool) async throws {
        let (store, request) = try await MainActor.run { () -> (AppStore, ToolPermissionRequest) in
            let store = try self.store.orClosing()
            return (store, try store.mobilePendingRequest(sessionId: sessionId, requestId: requestId, runId: runId))
        }
        await store.answerPermission(sessionId: sessionId, request: request, allow: allow)
        try await MainActor.run { try store.mobileCheckPermissionOutcome(sessionId: sessionId, requestId: requestId) }
    }
    func mobileAnswers(sessionId: String, requestId: String, runId: String, answers: [String: UserQuestionAnswer]) async throws {
        let (store, request) = try await MainActor.run { () -> (AppStore, ToolPermissionRequest) in
            let store = try self.store.orClosing()
            let request = try store.mobilePendingRequest(sessionId: sessionId, requestId: requestId, runId: runId)
            guard request.canAnswerQuestions else { throw MightyError("이 요청은 선택형 질문이 아닙니다.") }
            return (store, request)
        }
        await store.answerQuestionnaire(sessionId: sessionId, request: request, answers: answers)
        try await MainActor.run { try store.mobileCheckPermissionOutcome(sessionId: sessionId, requestId: requestId) }
    }
    func mobileCreateSession(workspaceId: String, kind: String, provider: String) async throws -> String {
        try await MainActor.run { try store.orClosing().mobileCreateSession(workspaceId: workspaceId, kind: kind, provider: provider) }
    }
}

private extension Optional where Wrapped == AppStore {
    func orClosing() throws -> AppStore {
        guard let store = self else { throw MightyError("앱이 종료 중입니다.") }
        return store
    }
}

extension AppStore {
    // MARK: Lifecycle

    func configureMobileRemote() {
        guard mobileBridge == nil else { return }
        let bridge = MobileRemoteBridge(store: self)
        mobileBridge = bridge
        let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0.1.0"
        // @Published fires before the property is assigned, so observe the
        // incoming values rather than re-reading the (still old) properties.
        $snapshot.sink { [weak self] value in self?.mobileObserve(snapshot: value) }.store(in: &mobileSubscriptions)
        $toolPermissions.sink { [weak self] value in self?.mobileObserve(permissions: value) }.store(in: &mobileSubscriptions)
        $queuedInputs.sink { [weak self] value in self?.mobileObserve(queued: value) }.store(in: &mobileSubscriptions)
        let settings = snapshot.mobileRemote ?? MobileRemoteSettings()
        Task {
            await mobileRemote.attach(bridge)
            await mobileRemote.setAppVersion(version)
            // The service reconnects on its own; the observer keeps the UI current.
            await mobileRemote.observeStatus { [weak self] status in Task { @MainActor in self?.mobileStatus = status } }
            mobileStatus = await mobileRemote.apply(settings: settings)
        }
    }

    func shutdownMobileRemote() async {
        mobileRetryTask?.cancel(); mobileSubscriptions.removeAll()
        await mobileRemote.shutdown()
    }

    func setMobileRemote(enabled: Bool, relayURL: String? = nil) {
        var settings = snapshot.mobileRemote ?? MobileRemoteSettings()
        settings.enabled = enabled
        if let relayURL { settings.relayURL = relayURL }
        settings = settings.normalized
        snapshot.mobileRemote = settings
        mobileBusy = true
        Task { mobileStatus = await mobileRemote.apply(settings: settings); mobileBusy = false }
    }

    func regenerateMobileKey() {
        mobileBusy = true
        Task {
            do { _ = try await mobileRemote.regenerateKey() } catch { self.error = error.localizedDescription }
            mobileStatus = await mobileRemote.apply(settings: snapshot.mobileRemote ?? MobileRemoteSettings())
            mobileBusy = false
        }
    }

    func refreshMobileStatus() { Task { mobileStatus = await mobileRemote.status() } }

    // MARK: Revisions

    func mobileObserve(snapshot incoming: AppSnapshot? = nil, permissions: [String: [ToolPermissionRequest]]? = nil, queued: [String: [QueuedInput]]? = nil) {
        guard !ending, mobileBridge != nil else { return }
        let snapshot = incoming ?? self.snapshot
        // Nothing to track while the feature is off; the first publish after
        // enabling re-seeds everything and bumps all revisions.
        guard snapshot.mobileRemote?.enabled == true else { if !mobileTracking.seen.isEmpty { mobileTracking = MobileRemoteTracking(stateRevision: mobileTracking.stateRevision) }; return }
        let permissions = permissions ?? toolPermissions
        let queued = queued ?? queuedInputs
        var changedSessions: [String] = []
        var stateChanged = mobileTracking.workspaces != snapshot.workspaces || mobileTracking.order != snapshot.sessions.map(\.id)
        var nextSeen: [String: MobileRemoteTracking.SessionFingerprint] = [:]
        var nextSummaries: [String: MobileSessionSummary] = [:]
        for session in snapshot.sessions {
            let fingerprint = MobileRemoteTracking.SessionFingerprint(session)
            let changed = mobileTracking.seen[session.id] != fingerprint
            if changed { mobileTracking.sessionRevisions[session.id, default: 0] += 1 }
            var summary = mobileSummary(session, revision: mobileTracking.sessionRevisions[session.id, default: 1], permissions: permissions, queued: queued)
            let previous = mobileTracking.summaries[session.id]
            var compare = summary; compare.revision = 0
            var previousCompare = previous; previousCompare?.revision = 0
            if previousCompare != compare {
                stateChanged = true
                if !changed { mobileTracking.sessionRevisions[session.id, default: 0] += 1 }
            }
            summary.revision = mobileTracking.sessionRevisions[session.id, default: 1]
            if previous?.revision != summary.revision { changedSessions.append(session.id) }
            nextSeen[session.id] = fingerprint; nextSummaries[session.id] = summary
        }
        for id in mobileTracking.summaries.keys where nextSummaries[id] == nil { mobileTracking.sessionRevisions.removeValue(forKey: id) }
        mobileTracking.seen = nextSeen; mobileTracking.summaries = nextSummaries; mobileTracking.workspaces = snapshot.workspaces
        mobileTracking.order = snapshot.sessions.map(\.id)
        if stateChanged { mobileTracking.stateRevision += 1 }
        guard stateChanged || !changedSessions.isEmpty else { return }
        let stateRevision = mobileTracking.stateRevision
        let sessionRevisions = changedSessions.map { ($0, mobileTracking.sessionRevisions[$0, default: 1]) }
        Task {
            if stateChanged { await mobileRemote.notify(scope: "state", revision: stateRevision) }
            for (id, revision) in sessionRevisions { await mobileRemote.notify(scope: "session:" + id, revision: revision) }
        }
    }

    private func mobileSummary(_ session: RunSession, revision: Int, permissions: [String: [ToolPermissionRequest]]? = nil, queued: [String: [QueuedInput]]? = nil) -> MobileSessionSummary {
        let pending = ((permissions ?? toolPermissions)[session.id] ?? []).filter { $0.state == "pending" }
        let last = session.logs.last(where: { $0.activity == nil && !$0.text.isEmpty })
        return MobileSessionSummary(
            id: session.id, workspaceId: session.workspaceId, title: session.title, kind: session.kind, provider: session.provider, model: session.model,
            status: session.status, revision: revision, updatedAt: session.logs.last?.timestamp ?? session.createdAt,
            preview: last.map { MobilePreview(kind: $0.kind, text: String($0.text.prefix(200))) },
            pendingPermissions: pending.filter { !$0.canAnswerQuestions }.count, pendingQuestions: pending.filter(\.canAnswerQuestions).count,
            queued: (queued ?? queuedInputs)[session.id]?.count ?? 0, resumeId: session.resumeId, terminal: usesLocalTerminal(session))
    }

    func mobileState() -> MobileState {
        MobileState(revision: mobileTracking.stateRevision, hostName: Host.current().localizedName ?? "MightyClaude Mac",
                    workspaces: snapshot.workspaces.map { MobileWorkspace(id: $0.id, name: $0.name, path: $0.path, remote: $0.remote != nil) },
                    sessions: snapshot.sessions.map { mobileTracking.summaries[$0.id] ?? mobileSummary($0, revision: mobileTracking.sessionRevisions[$0.id, default: 1]) })
    }

    func mobileSessionDetail(_ id: String) -> MobileSessionDetail? {
        guard let session = snapshot.sessions.first(where: { $0.id == id }) else { return nil }
        let revision = mobileTracking.sessionRevisions[id, default: 1]
        let summary = mobileTracking.summaries[id] ?? mobileSummary(session, revision: revision)
        let permissions = (toolPermissions[id] ?? []).filter { $0.state == "pending" }.map(MobilePermission.init(request:))
        let usage = session.sessionUsage.map { MobileUsage(model: $0.model, contextUsedTokens: $0.contextUsedTokens, contextWindowTokens: $0.contextWindowTokens,
                                                            contextPercent: $0.contextPercent, totalTokens: $0.totalTokens, costUSD: $0.costUSD) }
        return MobileSessionDetail(revision: revision, session: summary, entries: Array(session.logs.suffix(MobileSessionDetail.maximumEntries)),
                                   permissions: permissions, queued: (queuedInputs[id] ?? []).map { MobileQueuedItem(id: $0.id, text: $0.text) },
                                   usage: usage, elapsedSeconds: session.runTiming?.elapsed())
    }

    // MARK: Commands

    func mobileSubmit(_ id: String, text: String) throws -> String {
        guard !ending, !closingSessions.contains(id), let session = snapshot.sessions.first(where: { $0.id == id }),
              let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }) else { throw MightyError("실행 창을 찾을 수 없습니다.") }
        guard !usesLocalTerminal(session) else { throw MightyError("로컬 터미널 창에는 휴대폰에서 명령을 보낼 수 없습니다.") }
        if let reason = runBlockedReason(session) { throw MightyError(reason) }
        if session.status == "running" || pendingRuns.contains(id) {
            guard (queuedInputs[id]?.count ?? 0) < QueuedInput.maximumItems else { throw MightyError("대기열이 가득 찼습니다.") }
            let steers = canSteer(session)
            deferInput(id, session: session, workspace: workspace, item: QueuedInput(text: text))
            return steers ? "steered" : "queued"
        }
        error = nil
        guard start(id, session: session, workspace: workspace, input: text, attachments: [], restoringDraft: nil) else { throw MightyError(error ?? "실행을 시작하지 못했습니다.") }
        return "started"
    }

    /// Stop, permission and question routes act on an existing, non-terminal pane.
    func mobileValidateCommand(_ id: String) throws {
        guard !ending, !closingSessions.contains(id), let session = snapshot.sessions.first(where: { $0.id == id }) else { throw MightyError("실행 창을 찾을 수 없습니다.") }
        guard !usesLocalTerminal(session) else { throw MightyError("로컬 터미널 창은 휴대폰에서 제어할 수 없습니다.") }
    }

    func mobilePendingRequest(sessionId: String, requestId: String, runId: String) throws -> ToolPermissionRequest {
        try mobileValidateCommand(sessionId)
        guard let request = toolPermissions[sessionId]?.first(where: { $0.id == requestId && $0.runId == runId && $0.state == "pending" }) else {
            throw MightyError("대기 중인 권한 요청이 아닙니다. 이미 처리되었을 수 있습니다.")
        }
        return request
    }

    func mobileCheckPermissionOutcome(sessionId: String, requestId: String) throws {
        if toolPermissions[sessionId]?.contains(where: { $0.id == requestId && $0.state == "pending" }) == true {
            throw MightyError(permissionErrors[sessionId] ?? "권한 응답을 전달하지 못했습니다.")
        }
    }

    func mobileCreateSession(workspaceId: String, kind: String, provider: String) throws -> String {
        guard snapshot.workspaces.contains(where: { $0.id == workspaceId }) else { throw MightyError("워크스페이스를 찾을 수 없습니다.") }
        guard !hasModal else { throw MightyError("Mac에서 열린 창을 닫은 뒤 다시 시도하세요.") }
        error = nil
        guard let id = addSession(kind: kind, provider: provider, workspaceId: workspaceId) else { throw MightyError(error ?? "실행 창을 만들지 못했습니다.") }
        return id
    }
}

/// A QR image for the pairing URL, rendered with Core Image so the settings
/// sheet needs no extra dependency.
enum MobilePairingQR {
    static func image(for text: String, side: CGFloat = 220) -> NSImage? {
        guard let filter = CIFilter(name: "CIQRCodeGenerator") else { return nil }
        filter.setValue(Data(text.utf8), forKey: "inputMessage")
        filter.setValue("M", forKey: "inputCorrectionLevel")
        guard let output = filter.outputImage else { return nil }
        let scale = side / max(1, output.extent.width)
        let scaled = output.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
        let representation = NSCIImageRep(ciImage: scaled)
        let image = NSImage(size: representation.size)
        image.addRepresentation(representation)
        return image
    }
}
