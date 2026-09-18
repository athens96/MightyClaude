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
        /// The m1 extension's detail data. Settings, the pane's view style and
        /// the account limits all show on the phone, so each must wake a long
        /// poll even when the transcript did not move. The status line is
        /// deliberately absent: one that prints a clock or a token count would
        /// change on every run and the phone's poll would never sleep again.
        var settings: String; var view: String; var limits: String?
        /// Whether the phone may enable its settings pickers, and what they
        /// would offer. A phone holding a stale "editable" shows a control
        /// whose every value the host is about to refuse.
        var editable: Bool; var options: Int
        init(_ session: RunSession, editable: Bool, options: Int) {
            status = session.status; title = session.title; provider = session.provider; model = session.model; resumeId = session.resumeId
            count = session.logs.count
            tail = session.logs.suffix(12).map { "\($0.id):\($0.text.utf8.count):\($0.activity?.state ?? "")" }
            usage = session.sessionUsage?.updatedAt
            settings = session.settings.effort + "|" + session.settings.permissionMode
            view = (session.agentViewMode ?? "") + "|" + (session.mightyStyle ?? "")
            limits = session.sessionUsage?.rateLimitsUpdatedAt
            self.editable = editable; self.options = options
        }
    }
}

/// What a phone's submit actually did. Steering is only known once the runner
/// answers, so the task carries the verdict to the bridge.
enum MobileSubmitOutcome {
    case immediate(SubmitOutcome)
    case steering(Task<SubmitOutcome, Never>)
}

/// Hops every protocol call onto the main actor where the store lives.
final class MobileRemoteBridge: MobileHostDelegate, @unchecked Sendable {
    private weak var store: AppStore?
    init(store: AppStore) { self.store = store }

    func mobileState() async -> MobileState {
        await MainActor.run { store?.mobileState() ?? MobileState(revision: 0, hostName: "", workspaces: [], sessions: []) }
    }
    func mobileSession(id: String) async -> MobileSessionDetail? { await MainActor.run { store?.mobileSessionDetail(id) } }
    func mobileSubmit(sessionId: String, text: String, mode: String?) async throws -> String {
        let outcome = try await MainActor.run { try store.orClosing().mobileSubmit(sessionId, text: text, mode: mode) }
        let effect: SubmitOutcome
        switch outcome {
        case .immediate(let value): effect = value
        // The runner decides: a turn whose stdin already closed queues instead,
        // and settling that queue may have started the item right away.
        case .steering(let task): effect = await task.value
        }
        // Dropped: the text reached neither the turn nor the queue, so the phone
        // is told it failed instead of being shown an item that does not exist.
        guard let accepted = effect.accepted else { throw MobileHostError.conflict(MobileRemoteSupport.droppedMessage) }
        return accepted
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
    func mobileRemoveQueued(sessionId: String, itemId: String) async throws {
        try await MainActor.run { try store.orClosing().mobileRemoveQueued(sessionId, itemId: itemId) }
    }
    func mobileRunNextQueued(sessionId: String) async throws {
        try await MainActor.run { try store.orClosing().mobileRunNextQueued(sessionId) }
    }
    func mobileRename(sessionId: String, title: String) async throws {
        try await MainActor.run { try store.orClosing().mobileRename(sessionId, title: title) }
    }
    func mobileClose(sessionId: String) async throws {
        try await MainActor.run { try store.orClosing().mobileClose(sessionId) }
    }
    func mobileEntries(sessionId: String, before: String, limit: Int) async throws -> MobileEntriesPage {
        try await MainActor.run { try store.orClosing().mobileEntries(sessionId, before: before, limit: limit) }
    }
    func mobileApplySettings(sessionId: String, request: MobileSettingsRequest) async throws {
        try await MainActor.run { try store.orClosing().mobileApplySettings(sessionId, request: request) }
    }
    func mobileCommands(sessionId: String) async throws -> [MobileCommand] {
        // Awaits the first scan, so this one cannot be folded into MainActor.run.
        try await store.orClosing().mobileCommands(sessionId)
    }
    func mobilePerformCommand(sessionId: String, action: String) async throws -> String? {
        try await MainActor.run { try store.orClosing().mobilePerformCommand(sessionId, action: action) }
    }
}

private extension Optional where Wrapped == AppStore {
    func orClosing() throws -> AppStore {
        guard let store = self else { throw MightyError("앱이 종료 중입니다.") }
        return store
    }
}

extension AppStore {
    /// How stale a pane's drawn status line may be before a phone's read asks
    /// for a new one. Far above the Mac's own 2 s throttle on purpose.
    static let mobileStatusLineInterval: TimeInterval = 15

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
        // `$statusLines` is deliberately not observed: see SessionFingerprint.
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
            let editable = MobileRemoteSupport.editable(status: session.status, pendingRun: pendingRuns.contains(session.id))
            let fingerprint = MobileRemoteTracking.SessionFingerprint(session, editable: editable, options: mobileOptionsDigest(session))
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
            queued: (queued ?? queuedInputs)[session.id]?.count ?? 0, resumeId: session.resumeId, terminal: usesLocalTerminal(session),
            // A command pane has no agent view and no style; sending "plain"
            // and "cli" would invite the phone to offer pickers it cannot use.
            agentViewMode: session.kind == "shell" ? nil : mobileViewMode(session),
            mightyStyle: session.kind == "shell" ? nil : mobileStyle(session))
    }

    func mobileViewMode(_ session: RunSession) -> String { MobileRemoteSupport.viewMode(session.agentViewMode) }
    func mobileStyle(_ session: RunSession) -> String { MobileRemoteSupport.style(session.mightyStyle) }

    /// A cheap hash of the ids the phone's settings pickers would offer, built
    /// from the same call the detail sends so the two cannot drift.
    func mobileOptionsDigest(_ session: RunSession) -> Int {
        guard session.kind != "shell" else { return 0 }
        let options = mobileSettingsOptions(session)
        var ids = options.models.map(\.id) + options.permissionModes.map(\.id) + options.mightyStyles.map(\.id)
        ids += (options.efforts ?? []).map(\.id)
        return ids.joined(separator: "|").hashValue
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
        // The Mac re-runs the status line only for panes it is drawing; ask here
        // so a phone watching a hidden pane sees a live one too (only for the
        // local Claude panes that have a command at all). Only once what we hold
        // has gone stale: a status line printing a clock changes on every run,
        // and re-running it on every read would keep the phone polling forever.
        if Date().timeIntervalSince(statusLines[id]?.updatedAt ?? .distantPast) >= Self.mobileStatusLineInterval {
            refreshStatusLine(for: session)
        }
        return MobileSessionDetail(revision: revision, session: summary, entries: Array(session.logs.suffix(MobileSessionDetail.maximumEntries)),
                                   permissions: permissions, queued: (queuedInputs[id] ?? []).map { MobileQueuedItem(id: $0.id, text: $0.text) },
                                   usage: mobileUsage(session), elapsedSeconds: session.runTiming?.elapsed(),
                                   hasOlder: MobileRemoteSupport.hasOlder(entryCount: session.logs.count), settings: mobileSettings(session),
                                   statusLine: mobileStatusLine(id), rateLimits: MobileRemoteSupport.rateLimits(session.sessionUsage?.rateLimits ?? []))
    }

    private func mobileUsage(_ session: RunSession) -> MobileUsage? {
        session.sessionUsage.map { MobileUsage(model: $0.model, contextUsedTokens: $0.contextUsedTokens, contextWindowTokens: $0.contextWindowTokens,
                                               contextPercent: $0.contextPercent, totalTokens: $0.totalTokens, costUSD: $0.costUSD) }
    }

    private func mobileStatusLine(_ id: String) -> MobileStatusLine? {
        guard let result = statusLines[id]?.result, result.error == nil else { return nil }
        return MobileRemoteSupport.statusLine(result.lines)
    }

    // MARK: Settings

    /// The pickers the phone shows, filled from the same sources as the Mac's
    /// composer menus. `model` and `agentViewMode` let a request that also
    /// changes them be checked against the pane it is switching to, so one POST
    /// can turn Mighty view on and pick a guided style in the same body.
    func mobileSettingsOptions(_ session: RunSession, model: String? = nil, agentViewMode: String? = nil) -> MobileSettingsOptions {
        let runtime = providerRuntime(session.provider, workspaceId: session.workspaceId)
        let models = modelOptions(for: session).map { MobileOption(id: $0.value, label: $0.displayName) }
        let permissions = permissionModes(for: session).map { MobileOption(id: $0, label: permissionLabel($0, provider: session.provider)) }
        // No effort capability, no picker: "default" alone is not a choice.
        var efforts: [MobileOption]?
        if runtime.capabilities.effort {
            let levels = ProviderOptions.effortLevels(provider: session.provider, model: model ?? session.model, catalog: runtime.modelCatalog)
            efforts = [MobileOption(id: "default", label: effortLabel("default"))] + levels.map { MobileOption(id: $0, label: effortLabel($0)) }
        }
        let guided = mobileSupportsGuidedStyles(session, viewMode: agentViewMode ?? mobileViewMode(session))
        let styles = MobileRemoteSupport.styleOptionIds(guided: guided).map { MobileOption(id: $0, label: Self.mobileStyleLabel($0)) }
        return MobileSettingsOptions(models: models, permissionModes: permissions, efforts: efforts, mightyStyles: styles)
    }

    /// Guided styles exist only for local Claude panes in Mighty view — the
    /// same rule `guidedStyle(_:)` applies on the Mac.
    private func mobileSupportsGuidedStyles(_ session: RunSession, viewMode: String) -> Bool {
        let local = snapshot.workspaces.contains { $0.id == session.workspaceId && $0.remote == nil }
        return MobileRemoteSupport.guidedStylesAvailable(kind: session.kind, provider: session.provider, localWorkspace: local, viewMode: viewMode)
    }
    private static func mobileStyleLabel(_ style: String) -> String {
        switch style {
        case OuroborosFlow.style: return "Ouroboros"
        case PaperthinCatalog.style: return "Paperthin"
        default: return "CLI"
        }
    }

    func mobileSettings(_ session: RunSession) -> MobileSettings? {
        guard session.kind != "shell" else { return nil }
        let options = mobileSettingsOptions(session)
        let editable = MobileRemoteSupport.editable(status: session.status, pendingRun: pendingRuns.contains(session.id))
        return MobileSettings(editable: editable, model: session.model, permissionMode: session.settings.permissionMode,
                              effort: options.efforts == nil ? nil : session.settings.effort,
                              agentViewMode: mobileViewMode(session), mightyStyle: mobileStyle(session), options: options)
    }

    // MARK: Commands

    /// `mode: "queue"` only means "do not steer". A pane that is not running has
    /// nothing to drain its queue, so the request starts the run either way.
    func mobileSubmit(_ id: String, text: String, mode: String? = nil) throws -> MobileSubmitOutcome {
        guard !ending, !closingSessions.contains(id), let session = snapshot.sessions.first(where: { $0.id == id }),
              let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }) else { throw MightyError("실행 창을 찾을 수 없습니다.") }
        guard !usesLocalTerminal(session) else { throw MightyError("로컬 터미널 창에는 휴대폰에서 명령을 보낼 수 없습니다.") }
        if let reason = runBlockedReason(session) { throw MightyError(reason) }
        if session.status == "running" || pendingRuns.contains(id) {
            guard (queuedInputs[id]?.count ?? 0) < QueuedInput.maximumItems else { throw MightyError("대기열이 가득 찼습니다.") }
            let item = QueuedInput(text: text)
            let deferred = mobileCapturingError { deferInput(id, session: session, workspace: workspace, item: item, steering: mode != "queue") }
            switch deferred.value {
            case .steering(let task): return .steering(task)
            case .queued: return .immediate(.queued)
            case .refused: throw MightyError(deferred.failure ?? "대기열에 넣지 못했습니다.")
            }
        }
        let started = mobileCapturingError { start(id, session: session, workspace: workspace, input: text, attachments: [], restoringDraft: nil) }
        guard started.value else { throw MightyError(started.failure ?? "실행을 시작하지 못했습니다.") }
        return .immediate(.started)
    }

    /// Runs a store call that reports failures through the shared `error`
    /// banner and hands that text back instead. A phone must never clear or
    /// replace what the Mac is showing its own user.
    private func mobileCapturingError<T>(_ work: () -> T) -> (value: T, failure: String?) {
        let previous = error
        error = nil
        let value = work()
        let failure = error
        error = previous
        return (value, failure)
    }

    /// The pane a phone command acts on: present, not closing, not a terminal.
    /// Unknown is 404, "not from a phone" is 409.
    private func mobileCommandSession(_ id: String) throws -> RunSession {
        guard !ending, !closingSessions.contains(id), let session = snapshot.sessions.first(where: { $0.id == id }) else {
            throw MobileHostError.notFound("실행 창을 찾을 수 없습니다.")
        }
        guard !usesLocalTerminal(session) else { throw MobileHostError.conflict("로컬 터미널 창은 휴대폰에서 제어할 수 없습니다.") }
        return session
    }
    private func mobileAISession(_ id: String) throws -> RunSession {
        let session = try mobileCommandSession(id)
        guard session.kind != "shell" else { throw MobileHostError.conflict("명령 창에는 실행 설정과 슬래시 명령이 없습니다.") }
        return session
    }

    func mobileRemoveQueued(_ id: String, itemId: String) throws {
        _ = try mobileCommandSession(id)
        guard queuedInputs[id]?.contains(where: { $0.id == itemId }) == true else { throw MobileHostError.notFound("대기 중인 항목을 찾을 수 없습니다.") }
        removeQueuedInput(id, itemId: itemId)
    }

    func mobileRunNextQueued(_ id: String) throws {
        let session = try mobileCommandSession(id)
        guard !(queuedInputs[id] ?? []).isEmpty else { throw MobileHostError.conflict("대기 중인 요청이 없습니다.") }
        guard session.status != "running", !pendingRuns.contains(id) else { throw MobileHostError.conflict("실행이 끝난 뒤에 다음 요청을 시작할 수 있습니다.") }
        // Settling a blocked pane throws the whole queue away with only a log
        // line; the phone would be told "ok" and watch its requests vanish.
        if let reason = runBlockedReason(session) { throw MobileHostError.conflict(reason) }
        runNextQueuedInput(id)
    }

    func mobileRename(_ id: String, title: String) throws {
        _ = try mobileCommandSession(id)
        guard renameSession(id, to: title) else { throw MobileHostError.badRequest("실행 창 이름이 올바르지 않습니다.") }
    }

    func mobileClose(_ id: String) throws {
        _ = try mobileCommandSession(id)
        guard !hasModal else { throw MobileHostError.conflict("Mac에서 열린 창을 닫은 뒤 다시 시도하세요.") }
        closeSession(id)
    }

    func mobileEntries(_ id: String, before: String, limit: Int) throws -> MobileEntriesPage {
        guard let session = snapshot.sessions.first(where: { $0.id == id }) else { throw MobileHostError.notFound("실행 창을 찾을 수 없습니다.") }
        let page = MobileRemoteSupport.page(entries: session.logs, before: before, limit: limit)
        return MobileEntriesPage(entries: page.entries, hasMore: page.hasMore)
    }

    func mobileApplySettings(_ id: String, request: MobileSettingsRequest) throws {
        let session = try mobileAISession(id)
        // Shape and vocabulary first: a malformed body is 400 whatever the pane
        // is doing. The style is judged against the view mode this same request
        // asks for, so "turn Mighty on and pick Ouroboros" works in one POST.
        let viewMode = request.agentViewMode ?? mobileViewMode(session)
        try MobileRemoteSupport.validate(request, options: mobileSettingsOptions(session, model: request.model, agentViewMode: viewMode))
        guard session.status != "running", !pendingRuns.contains(id) else { throw MobileHostError.conflict("실행 중에는 설정을 바꿀 수 없습니다.") }
        guard !hasModal else { throw MobileHostError.conflict("Mac에서 열린 창을 닫은 뒤 다시 시도하세요.") }
        // Everything is checked before the first mutation, so a POST that will
        // fail leaves the pane exactly as the phone last saw it.
        try mobileCheckApplicable(session, request: request)
        if let model = request.model, model != session.model {
            changeModel(id, to: model)
            guard snapshot.sessions.first(where: { $0.id == id })?.model == model else { throw MobileHostError.conflict("모델을 바꾸지 못했습니다.") }
        }
        if let mode = request.agentViewMode { try mobileApplyViewMode(id, mode: mode) }
        if let style = request.mightyStyle { try mobileApplyStyle(id, style: style) }
        try mobileApplyRunSettings(id, effort: request.effort, permissionMode: request.permissionMode)
    }

    /// The conditions the underlying setters silently drop a change on, and the
    /// one `saveSettings` reports through `error`. Checked up front so none of
    /// them can leave the pane half-changed.
    private func mobileCheckApplicable(_ session: RunSession, request: MobileSettingsRequest) throws {
        if let mode = request.agentViewMode, mobileViewMode(session) != mode,
           !(session.kind == "claude" && MightyGraphSupport.providers.contains(session.provider)) {
            throw MobileHostError.conflict("이 실행 창은 Mighty 보기를 지원하지 않습니다.")
        }
        if let style = request.mightyStyle, mobileStyle(session) != style,
           !(session.kind == "claude" && session.provider == "claude") {
            throw MobileHostError.conflict("이 실행 창에서는 이 스타일을 쓸 수 없습니다.")
        }
        if let mode = request.permissionMode, mode != session.settings.permissionMode,
           !providerRuntime(session.provider, workspaceId: session.workspaceId).capabilities.permissionModes.contains(mode) {
            throw MobileHostError.conflict("이 실행 환경이 선택한 권한 모드를 지원하는지 확인하지 못했습니다.")
        }
    }

    private func mobileApplyViewMode(_ id: String, mode: String) throws {
        guard let session = snapshot.sessions.first(where: { $0.id == id }) else { throw MobileHostError.notFound("실행 창을 찾을 수 없습니다.") }
        guard mobileViewMode(session) != mode else { return }
        setAgentViewMode(id, mode: mode == MobileWire.plainViewMode ? "default" : "mighty")
        guard let updated = snapshot.sessions.first(where: { $0.id == id }), mobileViewMode(updated) == mode else {
            throw MobileHostError.conflict("이 실행 창은 Mighty 보기를 지원하지 않습니다.")
        }
    }

    private func mobileApplyStyle(_ id: String, style: String) throws {
        guard let session = snapshot.sessions.first(where: { $0.id == id }) else { throw MobileHostError.notFound("실행 창을 찾을 수 없습니다.") }
        guard mobileStyle(session) != style else { return }
        setMightyStyle(id, style: style == MobileWire.cliStyle ? nil : style)
        guard let updated = snapshot.sessions.first(where: { $0.id == id }), mobileStyle(updated) == style else {
            throw MobileHostError.conflict("이 실행 창에서는 이 스타일을 쓸 수 없습니다.")
        }
    }

    private func mobileApplyRunSettings(_ id: String, effort: String?, permissionMode: String?) throws {
        guard effort != nil || permissionMode != nil, let session = snapshot.sessions.first(where: { $0.id == id }) else { return }
        var settings = session.settings
        if let effort { settings.effort = effort }
        if let permissionMode { settings.permissionMode = permissionMode }
        guard settings != session.settings else { return }
        let saved = mobileCapturingError { saveSettings(id, settings: settings) }
        let applied = snapshot.sessions.first(where: { $0.id == id })?.settings
        guard applied?.effort == settings.effort, applied?.permissionMode == settings.permissionMode else {
            throw MobileHostError.conflict(saved.failure ?? "실행 설정을 적용하지 못했습니다.")
        }
    }

    func mobileCommands(_ id: String) async throws -> [MobileCommand] {
        let session = try mobileAISession(id)
        // The same catalogue the composer's palette lists. A workspace nobody
        // opened on the Mac has no scan yet, and answering from an empty cache
        // would tell the phone the pane has only built-ins.
        await awaitSlashCommands(for: session, timeout: 2)
        return MobileCommandSupport.wire(slashPalette(for: session, draft: "/"))
    }

    func mobilePerformCommand(_ id: String, action: String) throws -> String? {
        let session = try mobileAISession(id)
        switch action {
        case "help": return SlashCommandCatalog.helpText(provider: session.provider)
        case "usage": return MobileUsageText.text(usage: mobileUsage(session), model: session.model, elapsedSeconds: session.runTiming?.elapsed())
        case "clear":
            guard !hasModal else { throw MobileHostError.conflict("Mac에서 열린 창을 닫은 뒤 다시 시도하세요.") }
            guard session.status != "running", !pendingRuns.contains(id) else { throw MobileHostError.conflict("실행이 끝난 뒤에 새 대화로 시작할 수 있습니다.") }
            guard session.resumeId != nil else { throw MobileHostError.conflict("이어갈 이전 대화가 없습니다. 다음 입력은 이미 새 대화로 시작합니다.") }
            // Not `performSlashAction`: that selects the pane, so a phone would
            // move the Mac user's focus. The checks above are its guards.
            resetConversation(id)
            return nil
        default: throw MobileHostError.badRequest("지원하지 않는 명령입니다.")
        }
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
        guard let workspace = snapshot.workspaces.first(where: { $0.id == workspaceId }) else { throw MobileHostError.notFound("워크스페이스를 찾을 수 없습니다.") }
        // A local command pane runs in the app's own terminal, which the phone
        // cannot drive; creating one would hand it a dead window.
        guard kind != "shell" || workspace.remote != nil else { throw MobileHostError.conflict("로컬 워크스페이스의 명령 창은 휴대폰에서 쓸 수 없습니다.") }
        guard !hasModal else { throw MightyError("Mac에서 열린 창을 닫은 뒤 다시 시도하세요.") }
        let created = mobileCapturingError { addSession(kind: kind, provider: provider, workspaceId: workspaceId) }
        guard let id = created.value else { throw MightyError(created.failure ?? "실행 창을 만들지 못했습니다.") }
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
