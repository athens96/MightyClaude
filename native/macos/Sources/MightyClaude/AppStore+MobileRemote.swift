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
        /// The Mighty payload's own identity: block ids and statuses, the
        /// guided panel's state. Not the streamed
        /// text — see `AppStore.mobileMightyDigest`.
        var mighty: Int
        init(_ session: RunSession, editable: Bool, options: Int, mighty: Int) {
            status = session.status; title = session.title; provider = session.provider; model = session.model; resumeId = session.resumeId
            count = session.logs.count
            tail = session.logs.suffix(12).map { "\($0.id):\($0.text.utf8.count):\($0.activity?.state ?? "")" }
            usage = session.sessionUsage?.updatedAt
            settings = session.settings.effort + "|" + session.settings.permissionMode
            view = (session.agentViewMode ?? "") + "|" + (session.mightyStyle ?? "")
            limits = session.sessionUsage?.rateLimitsUpdatedAt
            self.editable = editable; self.options = options; self.mighty = mighty
        }
    }
}

/// What a phone's submit actually did. Releasing items held for the pane is
/// only known once its run starts, so the task carries the verdict to the bridge.
enum MobileSubmitOutcome {
    case immediate(SubmitOutcome)
    case pending(Task<SubmitOutcome, Never>)
}

/// Hops every protocol call onto the main actor where the store lives.
final class MobileRemoteBridge: MobileHostDelegate, @unchecked Sendable {
    private weak var store: AppStore?
    init(store: AppStore) { self.store = store }

    func mobileState() async -> MobileState {
        await MainActor.run { store?.mobileState() ?? MobileState(revision: 0, hostName: "", workspaces: [], sessions: []) }
    }
    func mobileSession(id: String) async -> MobileSessionDetail? { await MainActor.run { store?.mobileSessionDetail(id) } }
    func mobileSession(id: String, structure: Bool) async -> MobileSessionDetail? {
        await MainActor.run { store?.mobileSessionDetail(id, structure: structure) }
    }
    func mobileSubmit(sessionId: String, text: String, mode: String?, attachments: [RunAttachment]) async throws -> String {
        try await mobileSubmitReceipt(sessionId: sessionId, text: text, mode: mode, attachments: attachments).outcome
    }
    func mobileGuided(sessionId: String, style: String, skill: String, text: String) async throws -> String {
        try await mobileGuidedReceipt(sessionId: sessionId, style: style, skill: skill, text: text).outcome
    }
    func mobileSubmitReceipt(sessionId: String, text: String, mode: String?, attachments: [RunAttachment]) async throws -> MobileSubmitReceipt {
        try await settle(sessionId, MainActor.run { () -> (outcome: MobileSubmitOutcome, started: AppStore.StartedRequest?) in
            let store = try self.store.orClosing()
            return try (store.mobileSubmit(sessionId, text: text, mode: mode, attachments: attachments), store.startedRequests[sessionId])
        })
    }
    func mobileGuidedReceipt(sessionId: String, style: String, skill: String, text: String) async throws -> MobileSubmitReceipt {
        try await settle(sessionId, MainActor.run { () -> (outcome: MobileSubmitOutcome, started: AppStore.StartedRequest?) in
            let store = try self.store.orClosing()
            return try (store.mobileGuided(sessionId, style: style, skill: skill, text: text), store.startedRequests[sessionId])
        })
    }

    /// Turns what the store started into the word the phone is told, with the
    /// request's user entry and graph run when it started one at once. `call`
    /// pairs the outcome with the pane's newest started request, read in the same
    /// main-actor turn, so those ids are exactly the request sent.
    private func settle(_ sessionId: String, _ call: (outcome: MobileSubmitOutcome, started: AppStore.StartedRequest?)) async throws -> MobileSubmitReceipt {
        let effect: SubmitOutcome
        var started = call.started
        switch call.outcome {
        case .immediate(let value): effect = value
        // Held items released with the text: started, queued behind a run
        // that began meanwhile, or dropped. Its run began after the call and
        // another may have begun since, so it answers without ids: a wrong id
        // is worse than none (the phone matches by text and time instead).
        case .pending(let task):
            effect = await task.value
            started = nil
        }
        // Dropped: the text reached neither the turn nor the queue, so the phone
        // is told it failed instead of being shown an item that does not exist.
        guard let accepted = effect.accepted else { throw MobileHostError.conflict(MobileRemoteSupport.droppedMessage) }
        // Only a started request has a block of its own yet.
        guard effect == .started, let started else { return MobileSubmitReceipt(outcome: accepted) }
        return MobileSubmitReceipt(outcome: accepted, entryId: started.entryId, runId: started.runId)
    }
    func mobileStop(sessionId: String) async throws -> Bool {
        let (store, running) = try await MainActor.run { () -> (AppStore, Bool) in
            let store = try self.store.orClosing()
            try store.mobileValidateCommand(sessionId)
            return (store, store.mobileHasRun(sessionId))
        }
        guard running else { return false }
        // The child ends now; the bookkeeping below waits for the main actor.
        store.liveRuns.signalStop(id: sessionId)
        await store.stop(sessionId)
        return true
    }
    func mobilePermission(sessionId: String, requestId: String, runId: String, allow: Bool) async throws {
        let (store, request) = try await MainActor.run { () -> (AppStore, ToolPermissionRequest) in
            let store = try self.store.orClosing()
            return (store, try store.mobilePendingRequest(sessionId: sessionId, requestId: requestId, runId: runId))
        }
        await store.answerPermission(sessionId: sessionId, request: request, allow: allow)
        try await MainActor.run { try store.mobileCheckPermissionOutcome(sessionId: sessionId, requestId: requestId) }
    }
    /// An Antigravity card's answer (`allow` alone is "이번만 허용", with
    /// `always` "항상 허용"), or any other request's as before.
    func mobilePermission(sessionId: String, requestId: String, runId: String, allow: Bool, always: Bool) async throws {
        let (store, card) = try await MainActor.run { () -> (AppStore, AntigravityPermissionRequest?) in
            let store = try self.store.orClosing()
            try store.mobileValidateCommand(sessionId)
            return (store, store.antigravityBoard.pending(sessionId: sessionId, requestId: requestId, runId: runId))
        }
        guard let card else {
            guard !always else { throw MightyError(L("remote.error.notAntigravityPermission")) }
            try await mobilePermission(sessionId: sessionId, requestId: requestId, runId: runId, allow: allow)
            return
        }
        let decision = MobilePermissionAnswer(requestId: requestId, runId: runId, allow: allow, always: always).antigravityDecision
        if let failure = await store.answerAntigravityPermission(sessionId: sessionId, request: card, decision: decision, fromThisMac: false) { throw MightyError(failure) }
    }
    func mobileAnswers(sessionId: String, requestId: String, runId: String, answers: [String: UserQuestionAnswer]) async throws {
        let (store, request) = try await MainActor.run { () -> (AppStore, ToolPermissionRequest) in
            let store = try self.store.orClosing()
            let request = try store.mobilePendingRequest(sessionId: sessionId, requestId: requestId, runId: runId)
            guard request.canAnswerQuestions else { throw MightyError(L("remote.error.notQuestionnaire")) }
            return (store, request)
        }
        await store.answerQuestionnaire(sessionId: sessionId, request: request, answers: answers)
        try await MainActor.run { try store.mobileCheckPermissionOutcome(sessionId: sessionId, requestId: requestId) }
    }
    func mobilePlan(sessionId: String, requestId: String, runId: String, decision: PlanDecision) async throws {
        let (store, request) = try await MainActor.run { () -> (AppStore, ToolPermissionRequest) in
            let store = try self.store.orClosing()
            let request = try store.mobilePendingRequest(sessionId: sessionId, requestId: requestId, runId: runId)
            guard request.canAnswerPlan else { throw MightyError(L("plan.error.notPlan")) }
            return (store, request)
        }
        await store.answerPlan(sessionId: sessionId, request: request, decision: decision)
        try await MainActor.run { try store.mobileCheckPermissionOutcome(sessionId: sessionId, requestId: requestId) }
    }
    func mobileCreateSession(workspaceId: String, kind: String, provider: String) async throws -> String {
        try await MainActor.run { try store.orClosing().mobileCreateSession(workspaceId: workspaceId, kind: kind, provider: provider) }
    }
    func mobileRemoveQueued(sessionId: String, itemId: String) async throws {
        try await MainActor.run { try store.orClosing().mobileRemoveQueued(sessionId, itemId: itemId) }
    }
    func mobileRunNextQueued(sessionId: String) async throws {
        // Awaits delegation's release of held items, so it cannot be folded into MainActor.run.
        let store = try await MainActor.run { try self.store.orClosing() }
        try await store.mobileRunNextQueued(sessionId)
    }
    func mobileRename(sessionId: String, title: String, titleMode: String?) async throws {
        try await MainActor.run { try store.orClosing().mobileRename(sessionId, title: title, titleMode: titleMode) }
    }
    func mobileClose(sessionId: String) async throws {
        try await MainActor.run { try store.orClosing().mobileClose(sessionId) }
    }
    func mobileSetAskTarget(sessionId: String, target: String?) async throws {
        try await MainActor.run { try store.orClosing().mobileSetAskTarget(sessionId, target: target) }
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
    /// The phone's svg previews, drawn the way the files pane shows them
    /// (`NSImage`), at twice the point size up to the pixel cap. Runs off the
    /// main thread inside the preview read, and only for an svg that
    /// references nothing outside itself (`svgLoadsExternalContent`, checked
    /// again here); a size past `maximumVectorPoints` is not drawn.
    var mobileSVGRasterizer: MobileWorkspaceFiles.SVGRasterizer? {
        { data, maximumPixels in
            guard !FilePreviewClassifier.svgLoadsExternalContent(data), let image = NSImage(data: data),
                  FilePreviewClassifier.isDrawable(width: image.size.width, height: image.size.height) else { return nil }
            let scale = min(2, Double(maximumPixels) / Double(max(image.size.width, image.size.height)))
            let width = max(1, Int((image.size.width * scale).rounded())), height = max(1, Int((image.size.height * scale).rounded()))
            guard let context = MobileWorkspaceFiles.context(width: width, height: height, opaque: false) else { return nil }
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: false)
            image.draw(in: NSRect(x: 0, y: 0, width: width, height: height))
            NSGraphicsContext.restoreGraphicsState()
            return context.makeImage().map { ($0, image.size) }
        }
    }
}

private extension Optional where Wrapped == AppStore {
    func orClosing() throws -> AppStore {
        guard let store = self else { throw MightyError(L("settings.cliUpdate.detailClosing")) }
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
        let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0.2.0"
        // @Published fires before the property is assigned, so observe the
        // incoming values rather than re-reading the (still old) properties.
        $snapshot.sink { [weak self] value in self?.mobileObserve(snapshot: value) }.store(in: &mobileSubscriptions)
        $toolPermissions.sink { [weak self] value in self?.mobileObserve(permissions: value) }.store(in: &mobileSubscriptions)
        $antigravityBoard.sink { [weak self] value in self?.mobileObserve(antigravity: value) }.store(in: &mobileSubscriptions)
        // `$statusLines` is deliberately not observed: see SessionFingerprint.
        $queuedInputs.sink { [weak self] value in self?.mobileObserve(queued: value) }.store(in: &mobileSubscriptions)
        $delegationHeld.sink { [weak self] value in self?.mobileObserve(held: value) }.store(in: &mobileSubscriptions)
        // A child pane's parent and state ride in its summary.
        $delegationChildren.sink { [weak self] value in self?.mobileObserve(children: value) }.store(in: &mobileSubscriptions)
        configureMobilePush()
        // Asleep, the Mac missed the relay's pings and its control socket may be
        // gone on the relay's side while it still looks open here.
        NSWorkspace.shared.notificationCenter.publisher(for: NSWorkspace.didWakeNotification)
            .sink { [weak self] _ in guard let self else { return }; Task { await self.mobileRemote.reconnectSoon() } }
            .store(in: &mobileSubscriptions)
        let settings = snapshot.mobileRemote ?? MobileRemoteSettings()
        Task {
            await mobileRemote.attach(bridge)
            await mobileRemote.setAppVersion(version)
            // The service reconnects on its own; the observer keeps the UI current.
            await mobileRemote.observeStatus { [weak self] status in Task { @MainActor in self?.showMobileStatus(status) } }
            showMobileStatus(await mobileRemote.apply(settings: settings))
        }
    }

    func shutdownMobileRemote() async {
        mobileRetryTask?.cancel(); mobileSubscriptions.removeAll()
        mobilePushTask?.cancel(); mobilePushTask = nil
        await mobileRemote.shutdown()
    }

    func setMobileRemote(enabled: Bool, relayURL: String? = nil, allowLegacyPhones: Bool? = nil, directEnabled: Bool? = nil, directPort: Int? = nil) {
        var settings = snapshot.mobileRemote ?? MobileRemoteSettings()
        settings.enabled = enabled
        if let relayURL { settings.relayURL = relayURL }
        if let allowLegacyPhones { settings.allowLegacyPhones = allowLegacyPhones }
        if let directEnabled { settings.directEnabled = directEnabled }
        if let directPort { settings.directPort = directPort }
        settings = settings.normalized
        snapshot.mobileRemote = settings
        mobileBusy = true
        Task { showMobileStatus(await mobileRemote.apply(settings: settings)); mobileBusy = false }
    }

    func regenerateMobileKey() {
        mobileBusy = true
        Task {
            do { _ = try await mobileRemote.regenerateKey() } catch { self.error = error.localizedDescription }
            showMobileStatus(await mobileRemote.apply(settings: snapshot.mobileRemote ?? MobileRemoteSettings()))
            mobileBusy = false
        }
    }

    func refreshMobileStatus() { Task { showMobileStatus(await mobileRemote.status()) } }

    /// Every status travels its own hop to the main actor, and those hops can
    /// land out of order; an older one must never replace a newer one.
    func showMobileStatus(_ status: MobileHostStatus) {
        guard status.sequence >= mobileStatus.sequence else { return }
        mobileStatus = status
    }

    /// Unpairs one phone. The pairing key rotates with it, so the QR on screen
    /// changes and the revoked phone cannot pair again with the old one.
    func revokeMobileDevice(_ id: String) {
        mobileBusy = true
        Task {
            do { showMobileStatus(try await mobileRemote.revokeDevice(id)) }
            catch { self.error = error.localizedDescription; showMobileStatus(await mobileRemote.status()) }
            mobileBusy = false
        }
    }

    // MARK: Revisions

    func mobileObserve(snapshot incoming: AppSnapshot? = nil, permissions: [String: [ToolPermissionRequest]]? = nil, queued: [String: [QueuedInput]]? = nil,
                       held: [String: [DelegationDeliveryItem]]? = nil, children: [DelegationChildRow]? = nil, antigravity: AntigravityPermissionBoard? = nil) {
        guard !ending, mobileBridge != nil else { return }
        let snapshot = incoming ?? self.snapshot
        // Nothing to track while the feature is off; the first publish after
        // enabling re-seeds everything and bumps all revisions.
        guard snapshot.mobileRemote?.enabled == true else { if !mobileTracking.seen.isEmpty { mobileTracking = MobileRemoteTracking(stateRevision: mobileTracking.stateRevision) }; return }
        let permissions = permissions ?? toolPermissions
        let queued = queued ?? queuedInputs
        let held = held ?? delegationHeld
        // Panes whose held rows changed (`delegationHeld` is still the old map
        // here): one swapped for another leaves the summary's count as it was,
        // yet the phone must re-read the pane's queued array.
        let heldMoved = Set(held.keys).union(delegationHeld.keys).filter { held[$0]?.map(\.id) != delegationHeld[$0]?.map(\.id) }
        var changedSessions: [String] = []
        // The order carries the agent-owned terminal and browser panes too, so
        // opening or closing one of them is a state change the phone hears about.
        // Files panes are Mac-only and never listed for the phone.
        let phoneSessions = FilePaneKind.phoneVisible(RemoteWorkspacePhone.sessions(snapshot.sessions, workspaces: snapshot.workspaces))
        let paneOrder = phoneSessions.map(\.id) + AgentIOPaneRegistry.shared.extraPaneIds(agentSessions: snapshot.sessions)
        var stateChanged = mobileTracking.workspaces != snapshot.workspaces || mobileTracking.order != paneOrder
        var nextSeen: [String: MobileRemoteTracking.SessionFingerprint] = [:]
        var nextSummaries: [String: MobileSessionSummary] = [:]
        for session in phoneSessions {
            let editable = MobileRemoteSupport.editable(status: session.status, pendingRun: pendingRuns.contains(session.id))
            let fingerprint = MobileRemoteTracking.SessionFingerprint(session, editable: editable, options: mobileOptionsDigest(session), mighty: mobileMightyDigest(session))
            let changed = mobileTracking.seen[session.id] != fingerprint || heldMoved.contains(session.id)
            if changed { mobileTracking.sessionRevisions[session.id, default: 0] += 1 }
            var summary = mobileSummary(session, revision: mobileTracking.sessionRevisions[session.id, default: 1], permissions: permissions, queued: queued, held: held,
                                        children: children, antigravity: antigravity)
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
        mobileStructures.retain(Set(nextSummaries.keys))
        mobileTracking.seen = nextSeen; mobileTracking.summaries = nextSummaries; mobileTracking.workspaces = snapshot.workspaces
        mobileTracking.order = paneOrder
        if stateChanged { mobileTracking.stateRevision += 1 }
        guard stateChanged || !changedSessions.isEmpty else { return }
        let stateRevision = mobileTracking.stateRevision
        let sessionRevisions = changedSessions.map { ($0, mobileTracking.sessionRevisions[$0, default: 1]) }
        Task {
            if stateChanged { await mobileRemote.notify(scope: "state", revision: stateRevision) }
            for (id, revision) in sessionRevisions { await mobileRemote.notify(scope: "session:" + id, revision: revision) }
        }
    }

    private func mobileSummary(_ session: RunSession, revision: Int, permissions: [String: [ToolPermissionRequest]]? = nil, queued: [String: [QueuedInput]]? = nil,
                               held: [String: [DelegationDeliveryItem]]? = nil, children: [DelegationChildRow]? = nil, antigravity: AntigravityPermissionBoard? = nil) -> MobileSessionSummary {
        let pending = ((permissions ?? toolPermissions)[session.id] ?? []).filter { $0.state == "pending" }
        // An Antigravity card waits on a human like a permission does.
        let antigravityCards = (antigravity ?? antigravityBoard)[session.id] == nil ? 0 : 1
        // The sidebar's own test for a child waiting on a human (`WorkspaceView.paneRow`).
        let asksHuman = WorkDashboard.attention((permissions ?? toolPermissions)[session.id], antigravity: (antigravity ?? antigravityBoard)[session.id]).total > 0
        let child = MobileRemoteSupport.childLink(sessionId: session.id, children: children ?? delegationChildren, asksHuman: asksHuman)
        let last = MobileRemoteSupport.previewLine(session)
        return MobileSessionSummary(
            id: session.id, workspaceId: session.workspaceId, title: session.title, kind: session.kind, provider: session.provider, model: session.model,
            status: session.status, revision: revision, updatedAt: session.logs.last?.timestamp ?? session.createdAt,
            preview: last.map { MobilePreview(kind: $0.kind, text: String($0.text.prefix(200))) },
            pendingPermissions: pending.filter { !$0.canAnswerQuestions }.count + antigravityCards, pendingQuestions: pending.filter(\.canAnswerQuestions).count,
            // Held rows are in the pane's queued list too.
            queued: ((queued ?? queuedInputs)[session.id]?.count ?? 0) + ((held ?? delegationHeld)[session.id]?.count ?? 0), resumeId: session.resumeId, terminal: usesLocalTerminal(session) || AgentIOPaneKind.isAgentIOPane(session.kind),
            // A command pane has no agent view and no style; sending "plain"
            // and "cli" would invite the phone to offer pickers it cannot use.
            agentViewMode: session.kind == "shell" ? nil : mobileViewMode(session),
            mightyStyle: session.kind == "shell" ? nil : mobileStyle(session),
            styleId: session.kind == "shell" ? nil : mobileStyleId(session),
            titleMode: session.titleMode ?? "auto",
            resolvedModel: MobileRemoteSupport.resolvedModel(session, catalog: providerRuntime(session.provider, workspaceId: session.workspaceId).modelCatalog),
            // Always false since 0.4.35 (a busy pane only queues), for older phones.
            canSteer: false,
            parentSessionId: child?.parent, childState: child?.state)
    }

    func mobileViewMode(_ session: RunSession) -> String { MobileRemoteSupport.viewMode(session.agentViewMode) }
    /// The truth: the style this pane is actually running. Unapproved,
    /// revoked, unregistered, hash-mismatched or remote all answer "cli", so
    /// nothing §4.5 hid leaks through a session summary (§7.2).
    func mobileStyleId(_ session: RunSession) -> String { guidedStyle(session)?.id ?? MobileWire.cliStyle }
    /// The old two-word field (`cli`, `ouroboros`), which only the bundled
    /// Ouroboros fills with its own word; every other style sends `cli`.
    func mobileStyle(_ session: RunSession) -> String { MobileRemoteSupport.style(mobileStyleId(session)) }

    /// A cheap hash of the ids the phone's settings pickers would offer, built
    /// from the same call the detail sends so the two cannot drift.
    func mobileOptionsDigest(_ session: RunSession) -> Int {
        guard session.kind != "shell" else { return 0 }
        let options = mobileSettingsOptions(session)
        // Labels too: a catalogue that arrives later relabels the rows (`Opus` → `Opus 5.5`).
        var ids = options.models.map { $0.id + "\u{1F}" + $0.label } + options.permissionModes.map(\.id) + options.mightyStyles.map(\.id)
        ids += options.styles.map { $0.id + "|" + ($0.source?.rawValue ?? "") }
        ids += (options.efforts ?? []).map(\.id)
        // The composer's "send to" switch: the targets offered and the one in use.
        let targets = askTargets(for: session)
        ids += targets.map { "ask:" + $0.id }
        ids.append("askTarget:" + (AskTargets.effective(saved: session.askTarget, available: targets)?.id ?? ""))
        return ids.joined(separator: "|").hashValue
    }

    func mobileState() -> MobileState {
        let revision = mobileTracking.stateRevision
        var sessions = FilePaneKind.phoneVisible(RemoteWorkspacePhone.sessions(snapshot.sessions, workspaces: snapshot.workspaces)).map { mobileTracking.summaries[$0.id] ?? mobileSummary($0, revision: mobileTracking.sessionRevisions[$0.id, default: 1]) }
        sessions.append(contentsOf: AgentIOPaneRegistry.shared.extraPaneSummaries(
            agentSessions: snapshot.sessions,
            revision: revision,
            updatedAt: mightyTimestamp()
        ))
        return MobileState(revision: revision, hostName: Host.current().localizedName ?? "Mighty Claude Mac",
                           workspaces: RemoteWorkspacePhone.workspaces(snapshot.workspaces).map { MobileWorkspace(id: $0.id, name: $0.name, path: $0.path) },
                           sessions: sessions)
    }

    /// A pane the phone may see and act on by id; files panes are Mac-only.
    func mobilePane(_ id: String) -> RunSession? {
        snapshot.sessions.first { $0.id == id && !FilePaneKind.isFilePane($0.kind) && !isRemoteWorkspace($0.workspaceId) }
    }

    /// `structure`: the phone asked for the structure view (`?structure=`); only then is it built.
    func mobileSessionDetail(_ id: String, structure: Bool = false) -> MobileSessionDetail? {
        guard let session = mobilePane(id) else { return nil }
        let revision = mobileTracking.sessionRevisions[id, default: 1]
        let summary = mobileTracking.summaries[id] ?? mobileSummary(session, revision: revision)
        let permissions = (toolPermissions[id] ?? []).filter { $0.state == "pending" }.map(MobilePermission.init(request:))
            + (antigravityBoard[id].map { [MobilePermission(antigravity: $0)] } ?? [])
        // The Mac re-runs the status line only for panes it is drawing; ask here
        // so a phone watching a hidden pane sees a live one too (only for the
        // local Claude panes that have a command at all). Only once what we hold
        // has gone stale: a status line printing a clock changes on every run,
        // and re-running it on every read would keep the phone polling forever.
        if Date().timeIntervalSince(statusLines[id]?.updatedAt ?? .distantPast) >= Self.mobileStatusLineInterval {
            refreshStatusLine(for: session)
        }
        let targets = askTargets(for: session)
        return MobileSessionDetail(revision: revision, session: summary, entries: Array(session.logs.suffix(MobileSessionDetail.maximumEntries)),
                                   permissions: permissions, queued: DelegationQueueRows.mobileItems(held: delegationHeld[id] ?? [], queued: queuedInputs[id] ?? []),
                                   usage: mobileUsage(session), elapsedSeconds: session.runTiming?.elapsed(),
                                   hasOlder: MobileRemoteSupport.hasOlder(entryCount: session.logs.count), settings: mobileSettings(session),
                                   mighty: mobileMighty(session, structure: structure),
                                   statusLine: mobileStatusLine(id), rateLimits: MobileRemoteSupport.rateLimits(session.sessionUsage?.rateLimits ?? []),
                                   askTargets: targets.map { MobileAskTarget(id: $0.id, label: $0.label) },
                                   askTarget: AskTargets.effective(saved: session.askTarget, available: targets)?.id)
    }

    // MARK: Mighty

    /// The Mighty view of a pane: its style, its newest runs as blocks, and the
    /// guided panel the Mac would draw beside the composer. Absent for panes
    /// the Mac is not showing in Mighty view — the phone shows the transcript.
    func mobileMighty(_ session: RunSession, structure: Bool = false) -> MobileMighty? {
        guard MobileRemoteSupport.sendsMighty(kind: session.kind, agentViewMode: session.agentViewMode) else { return nil }
        let style = mobileStyle(session)
        // The saved graph as it stands, not `mightyGraphRuns`: that property
        // copies every entry of every run to stamp the provider on it, which a
        // phone's payload never reads and a poll must not pay for.
        let saved = session.graphRuns ?? MightyGraphSupport.legacyRuns(session)
        // Only a pane that really runs an approved style carries a panel (§7.3)
        // — and only such a pane gets style prefixes on its request blocks, so
        // the registry sweep is behind the same gate (§1.10).
        guard let registered = guidedStyle(session) else {
            let built = structure ? mobileStructure(session, runs: saved, style: nil) : nil
            return MobileMighty(style: style, runs: MobileMightySupport.runs(saved, outputTails: backgroundOutput.lines(forPane: session.id)) { _ in nil },
                                structure: built?.value, structureTag: built?.tag)
        }
        let runnable = styleRegistry.runnableInPrecedence(workspace: styleWorkspaceRef(session))
        let titles = StyleRequestTitles(styles: runnable)
        let runs = MobileMightySupport.runs(saved, outputTails: backgroundOutput.lines(forPane: session.id)) { titles.prefix($0) }
        // The Mac reads these when the style is chosen; a pane only ever
        // watched from a phone has never triggered that read.
        if stylePrerequisite(registered, for: session) == nil { refreshStylePrerequisites(registered, for: session) }
        if !styleStateIsLoaded(registered, for: session) { refreshStyleState(registered, for: session) }
        let panel = mobileStylePanel(registered, for: session, runs: saved)
        let built = structure ? mobileStructure(session, runs: saved, style: registered, titles: titles, runnable: runnable) : nil
        return MobileMighty(style: style, styleId: registered.id, runs: runs, panel: panel,
                            ouroboros: MobileLegacyStyleAdapter.payload(style: registered, panel: panel),
                            structure: built?.value, structureTag: built?.tag)
    }

    /// The structure view the Mac pane draws (`MightyGraphView.structureShape`), from the
    /// same inputs: the pane's phase as its phase bar reads it, its answered plans and the
    /// plan waiting for an answer. The Mac's "start anew" choice lives in the pane's own
    /// view and never reaches here, so the phone always reads the requests already there.
    /// Older requests the pane loaded from its CLI's record are not sent, as with `runs`.
    /// The cache key names every input beyond the graph: the styles the request titles are
    /// read from (`runnable`) and the model catalogue the node labels are read from.
    private func mobileStructure(_ session: RunSession, runs: [MightyGraphRun], style: RegisteredStyle?,
                                 titles: StyleRequestTitles? = nil, runnable: [RegisteredStyle] = []) -> MobileStructureCache.Built {
        let phase = style.flatMap {
            $0.evaluator.currentPhase(session: session, fileSourceStates: styleFileStates($0, for: session), planStage: stylePlanStage($0, for: session))
        }
        let catalog = providerRuntime(session.provider, workspaceId: session.workspaceId).modelCatalog.models
        return mobileStructures.structure(pane: session.id, runs: runs, style: style?.evaluator, currentPhase: phase,
                                          planHistory: session.planHistory ?? [],
                                          pendingPlanRunID: PlanCardSupport.pendingPlan(toolPermissions[session.id])?.runId,
                                          key: mobileStructureKey(style: style, runnable: runnable, catalog: catalog),
                                          tails: backgroundOutput.lines(forPane: session.id),
                                          title: { titles?.prefix($0) },
                                          model: { ModelUsageFormat.shortName($0, catalog: catalog, versioned: true) })
    }

    /// The very projection the Mac panel is drawn from. The phone has no
    /// group of its own yet, so the style's initial-group rule decides.
    func mobileStylePanel(_ style: RegisteredStyle, for session: RunSession, runs: [MightyGraphRun]? = nil) -> StylePanel {
        let prompts = (runs ?? session.graphRuns ?? MightyGraphSupport.legacyRuns(session)).map(\.input)
        // A style whose probes declare nothing is ready; anything else has not
        // been answered yet, and "ready" is the answer that hides the setup
        // notice and offers buttons that would fail (§1.5).
        let unknown = StylePrerequisiteResult(ready: style.manifest.prerequisites.probes.isEmpty)
        // No built-in feature exists to read (§1.8), so there is no state to pass.
        return StylePanelProjection.make(style: style, prompts: prompts, selectedGroupId: nil,
                                         capabilityStates: [:],
                                         prerequisites: stylePrerequisite(style, for: session) ?? unknown,
                                         running: session.status == "running",
                                         session: session,
                                         // §1.16: the same reading the Mac pane's phase bar uses.
                                         state: styleState(style, for: session))
    }

    private func mobileStructureKey(style: RegisteredStyle?, runnable: [RegisteredStyle], catalog: [ModelOption]) -> String {
        var hasher = Hasher()
        hasher.combine(style?.id); hasher.combine(style?.hash)
        for each in runnable { hasher.combine(each.id); hasher.combine(each.hash) }
        for option in catalog { hasher.combine(option.value); hasher.combine(option.displayName); hasher.combine(option.resolvedModel) }
        return String(hasher.finalize())
    }

    /// What a phone watching the Mighty view would notice change: the run and
    /// block identities with their statuses, the guided panel's own state. The
    /// streamed text is deliberately absent, or a long poll would wake on every
    /// token and never sleep again.
    func mobileMightyDigest(_ session: RunSession) -> Int {
        guard MobileRemoteSupport.sendsMighty(kind: session.kind, agentViewMode: session.agentViewMode) else { return 0 }
        var hasher = Hasher()
        // The same source `mobileMighty` sends, reading a legacy pane's runs
        // by identity alone rather than rebuilding them: this runs on every
        // snapshot publish — during a stream, every token.
        hasher.combine(MobileMightySupport.digest(session: session))
        // A background run's last output lines live on this Mac alone, beside the graph:
        // their revision, not the lines, which moves at most once per 3 s a pane.
        hasher.combine(backgroundOutput.phoneRevision(forPane: session.id))
        // An answered plan moves a request's phase in the structure view.
        hasher.combine(session.planHistory?.count); hasher.combine(session.planHistory?.last?.outcome.rawValue)
        guard let style = guidedStyle(session) else { return hasher.finalize() }
        // The style itself, so a revocation or a changed manifest wakes the poll.
        hasher.combine(style.id); hasher.combine(style.hash)
        // The requests `currentPhase(session:)` would read, without rebuilding
        // a legacy pane's runs: their inputs are its user entries, and that is
        // the fallback the phase itself uses.
        var prompts: [String] = []
        if let saved = session.graphRuns { prompts = saved.map(\.input) }
        else { prompts = session.logs.filter { $0.kind == "user" }.map(\.text) }
        // §1.16: what the state sources read moves the phase and the widgets.
        let state = styleState(style, for: session)
        hasher.combine(style.evaluator.currentPhase(prompts: prompts, fileSourceStates: state.fileSourceStates, planStage: state.planStage)?.id)
        hasher.combine(state)
        // A sequence's chips disappear while it runs, and its guidance line
        // changes with it (§6.1).
        hasher.combine(session.status == "running")
        let setup = stylePrerequisite(style, for: session)
        hasher.combine(setup?.ready); hasher.combine(setup?.missing)
        return hasher.finalize()
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
            let levels = ProviderOptions.effortLevels(provider: session.provider, model: model ?? session.model, catalog: runtime.modelCatalog, registeredModels: providerRegisteredModels(session.provider))
            efforts = [MobileOption(id: "default", label: effortLabel("default"))] + levels.map { MobileOption(id: $0, label: effortLabel($0)) }
        }
        let guided = mobileSupportsGuidedStyles(session, viewMode: agentViewMode ?? mobileViewMode(session))
        let runnable = guided ? applicableStyles(session, viewMode: agentViewMode).filter(\.isRunnable) : []
        // The old field keeps its fixed vocabulary, but it may only offer what
        // this pane could actually pick: with the resource bundle missing the
        // bundled Ouroboros does not exist, and offering it would let `validate`
        // accept an id `mobileApplyStyleId` then refuses (§7.2).
        let legacyIds = [MobileWire.cliStyle] + MobileWire.mightyStyles.filter { id in
            id != MobileWire.cliStyle && runnable.contains { $0.id == id }
        }
        let legacy = legacyIds.map { MobileOption(id: $0, label: Self.mobileStyleLabel($0)) }
        return MobileSettingsOptions(models: models, permissionModes: permissions, efforts: efforts,
                                     mightyStyles: legacy, styles: MobileRemoteSupport.styleOptions(runnable))
    }

    /// The pane's own applicable list, judged against the view mode this
    /// request is switching to, so one POST can turn Mighty on and pick a style.
    private func applicableStyles(_ session: RunSession, viewMode: String?) -> [RegisteredStyle] {
        guard session.kind == "claude", session.provider == "claude" else { return [] }
        guard (viewMode ?? mobileViewMode(session)) == "mighty" else { return [] }
        return styleRegistry.applicable(workspace: styleWorkspaceRef(session))
    }

    /// Guided styles exist only for local Claude panes in Mighty view — the
    /// same rule `guidedStyle(_:)` applies on the Mac.
    private func mobileSupportsGuidedStyles(_ session: RunSession, viewMode: String) -> Bool {
        MobileRemoteSupport.guidedStylesAvailable(kind: session.kind, provider: session.provider, viewMode: viewMode)
    }
    /// The bundled two keep the labels the old field always carried.
    private static func mobileStyleLabel(_ style: String) -> String {
        BundledStyles.shared.manifest(style)?.name ?? StyleMenu.cliLabel
    }

    func mobileSettings(_ session: RunSession) -> MobileSettings? {
        guard session.kind != "shell" else { return nil }
        let options = mobileSettingsOptions(session)
        let editable = MobileRemoteSupport.editable(status: session.status, pendingRun: pendingRuns.contains(session.id))
        return MobileSettings(editable: editable, model: session.model, permissionMode: session.settings.permissionMode,
                              effort: options.efforts == nil ? nil : session.settings.effort,
                              agentViewMode: mobileViewMode(session), mightyStyle: mobileStyle(session),
                              styleId: mobileStyleId(session), options: options)
    }

    // MARK: Commands

    /// A busy pane always queues the text as its next request and says
    /// `queued`, whatever `mode` asks for: nothing a person sends joins the
    /// running turn, and `mode: "steer"` from an older phone is queued too. A
    /// pane that is not running has nothing to drain its queue, so the request
    /// starts the run either way. The Mac composer's draft and attachments
    /// are left alone: the phone's text never came from them.
    func mobileSubmit(_ id: String, text: String, mode: String? = nil, attachments: [RunAttachment] = []) throws -> MobileSubmitOutcome {
        guard !ending, !closingSessions.contains(id), let session = mobilePane(id),
              let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }) else { throw MightyError(L("remote.error.paneNotFound")) }
        guard !usesLocalTerminal(session) else { throw MightyError(L("remote.error.localTerminalInput")) }
        guard !text.isEmpty || !attachments.isEmpty else { throw MightyError(L("remote.error.nothingToSend")) }
        // The pane's "send to" choice wraps the phone's text as it does the composer's.
        var text = text
        if askSendTarget(session) != nil {
            guard attachments.isEmpty else { throw MightyError(L("composer.ask.attachmentsOff")) }
            guard let wrapped = askSendText(text, session: session) else { throw MightyError(L("remote.error.nothingToSend")) }
            text = wrapped
        }
        if let reason = runBlockedReason(session, checkRuntime: false) { throw MightyError(reason) }
        let held = backgroundUpdateHolds(session)
        if session.status == "running" || pendingRuns.contains(id) || held {
            guard (queuedInputs[id]?.count ?? 0) < QueuedInput.maximumItems else { throw MightyError(L("remote.error.queueFull")) }
            // The composer's rule: a plan-mode style's queued request plans fresh (§1.17.4).
            let plans = styleLaunchesInPlanMode(session)
            // The phone's send is not this Mac's own: when it starts, the Mac's views only follow.
            let item = QueuedInput(text: text, attachments: attachments, permissionModeOverride: BackgroundQueuePolicy.queuedOverride(launchesInPlan: plans), own: false)
            if held { heldForUpdate.insert(id) }
            let deferred = mobileCapturingError { deferInput(id, session: session, workspace: workspace, item: item, takingDraft: false) }
            switch deferred.value {
            case .queued: return .immediate(.queued)
            case .refused: throw MightyError(deferred.failure ?? L("remote.error.queueRefused"))
            }
        }
        // Items held for the pane go first, in this one run.
        if !(delegationHeld[id] ?? []).isEmpty { return .pending(releaseHeld(id, input: text, attachments: attachments, from: .phone)) }
        let started = mobileCapturingError { start(id, session: session, workspace: workspace, input: text, attachments: attachments, restoringDraft: nil, own: false) }
        guard started.value else { throw MightyError(started.failure ?? L("remote.error.startFailed")) }
        return .immediate(.started)
    }

    /// A guided style's button, pressed from the phone. The prompt is built by
    /// the very functions the Mac's own buttons use and then travels the
    /// ordinary submit path, so `accepted` means exactly what it does there and
    /// the draft the Mac user is typing is left alone.
    func mobileGuided(_ id: String, style: String, skill: String, text: String) throws -> MobileSubmitOutcome {
        let session = try mobileAISession(id)
        switch MobileRemoteSupport.guidedDecision(registry: styleRegistry, workspace: styleWorkspaceRef(session),
                                                  pane: guidedStyle(session), styleId: style, actionId: skill, text: text) {
        case .unknownStyle: throw MobileHostError.badRequest(MobileRemoteSupport.unknownStyleMessage)
        case .otherPane(let styleId): throw MobileHostError.conflict(L("remote.error.otherStyle", ["style": styleId]))
        case .unknownAction: throw MobileHostError.badRequest(L("remote.error.unknownSkill"))
        case .send(let prompt): return try mobileSubmit(id, text: prompt)
        }
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
        guard !ending, !closingSessions.contains(id), let session = mobilePane(id) else {
            throw MobileHostError.notFound(L("remote.error.paneNotFound"))
        }
        guard !usesLocalTerminal(session) else { throw MobileHostError.conflict(L("remote.error.localTerminalControl")) }
        return session
    }
    private func mobileAISession(_ id: String) throws -> RunSession {
        let session = try mobileCommandSession(id)
        guard session.kind != "shell" else { throw MobileHostError.conflict(L("remote.error.shellNoSettings")) }
        return session
    }

    /// A held row is refused with `held_not_removable`, and nothing changes.
    func mobileRemoveQueued(_ id: String, itemId: String) throws {
        _ = try mobileCommandSession(id)
        if DelegationQueueRows.removeRefusal(itemId: itemId, held: delegationHeld[id] ?? []) == .heldNotRemovable {
            throw MobileHostError.conflict(L("queue.heldNotRemovable"))
        }
        guard queuedInputs[id]?.contains(where: { $0.id == itemId }) == true else { throw MobileHostError.notFound(L("remote.error.queueItemNotFound")) }
        removeQueuedInput(id, itemId: itemId)
    }

    /// Items held for the pane run next, all of them in one run (steered into
    /// its running Claude run when it has one); the queued rows follow later.
    func mobileRunNextQueued(_ id: String) async throws {
        let session = try mobileCommandSession(id)
        let held = !(delegationHeld[id] ?? []).isEmpty
        guard held || !(queuedInputs[id] ?? []).isEmpty else { throw MobileHostError.conflict(L("remote.error.queueEmpty")) }
        if held {
            if let reason = runBlockedReason(session, checkRuntime: false) { throw MobileHostError.conflict(reason) }
            let macError = error
            let released = await runNextHeld(id)
            let failure = error == macError ? nil : error
            error = macError
            guard released else { throw MobileHostError.conflict(failure ?? L("remote.error.startFailed")) }
            return
        }
        // A run that only waits on background work takes the first request as a new turn, as the desktop's run next does.
        if await steerQueuedRow(id, own: false) { return }
        guard session.status != "running", !pendingRuns.contains(id) else { throw MobileHostError.conflict(L("remote.error.nextAfterRun")) }
        // Settling a blocked pane throws the whole queue away with only a log
        // line; the phone would be told "ok" and watch its requests vanish.
        if let reason = runBlockedReason(session, checkRuntime: false) { throw MobileHostError.conflict(reason) }
        // The phone's 다음 실행: the Mac's views only follow it.
        runNextQueuedInput(id, own: false)
    }

    func mobileRename(_ id: String, title: String, titleMode: String?) throws {
        _ = try mobileCommandSession(id)
        if titleMode == "auto" {
            setSessionAutoTitle(id)
        } else {
            guard renameSession(id, to: title) else { throw MobileHostError.badRequest(L("remote.error.badTitle")) }
        }
    }

    /// The phone's "send to" switch: a target this pane offers, or nil for Claude.
    func mobileSetAskTarget(_ id: String, target: String?) throws {
        let session = try mobileAISession(id)
        if let target, !askTargets(for: session).contains(where: { $0.id == target }) { throw MobileHostError.badRequest(L("remote.error.badBody")) }
        setAskTarget(id, target: target)
    }

    func mobileClose(_ id: String) throws {
        _ = try mobileCommandSession(id)
        guard !hasBlockingModal else { throw MobileHostError.conflict(L("remote.error.closeMacWindow")) }
        closeSession(id)
    }

    func mobileEntries(_ id: String, before: String, limit: Int) throws -> MobileEntriesPage {
        guard let session = mobilePane(id) else { throw MobileHostError.notFound(L("remote.error.paneNotFound")) }
        let page = MobileRemoteSupport.page(entries: session.logs, before: before, limit: limit)
        return MobileEntriesPage(entries: page.entries, hasMore: page.hasMore)
    }

    func mobileApplySettings(_ id: String, request: MobileSettingsRequest) throws {
        let session = try mobileAISession(id)
        // Shape and vocabulary first: a malformed body is 400 whatever the pane
        // is doing. The style is judged against the view mode this same request
        // asks for, so "turn Mighty on and pick Ouroboros" works in one POST.
        let viewMode = request.agentViewMode ?? mobileViewMode(session)
        // What this sender was last told, read before a single mutation.
        let shown = mobileStyleId(session)
        try MobileRemoteSupport.validate(request, options: mobileSettingsOptions(session, model: request.model, agentViewMode: viewMode))
        guard session.status != "running", !pendingRuns.contains(id) else { throw MobileHostError.conflict(L("phone.session.settingsLocked")) }
        guard !hasBlockingModal else { throw MobileHostError.conflict(L("remote.error.closeMacWindow")) }
        // Everything is checked before the first mutation, so a POST that will
        // fail leaves the pane exactly as the phone last saw it.
        try mobileCheckApplicable(session, request: request)
        if let model = request.model, model != session.model {
            changeModel(id, to: model)
            guard mobilePane(id)?.model == model else { throw MobileHostError.conflict(L("remote.error.modelFailed")) }
        }
        if let mode = request.agentViewMode { try mobileApplyViewMode(id, mode: mode) }
        // `styleId` is the open field and wins outright; `mightyStyle` is then
        // ignored rather than refused, so the host's own pair round-trips (§7.2).
        if let styleId = request.styleId { try mobileApplyStyleId(id, styleId: styleId, shown: shown) }
        else if let style = request.mightyStyle { try mobileApplyStyleId(id, styleId: style, shown: shown) }
        try mobileApplyRunSettings(id, effort: request.effort, permissionMode: request.permissionMode)
    }

    /// The conditions the underlying setters silently drop a change on, and the
    /// one `saveSettings` reports through `error`. Checked up front so none of
    /// them can leave the pane half-changed.
    private func mobileCheckApplicable(_ session: RunSession, request: MobileSettingsRequest) throws {
        if let mode = request.agentViewMode, mobileViewMode(session) != mode,
           !(session.kind == "claude" && MightyGraphSupport.providers.contains(session.provider)) {
            throw MobileHostError.conflict(L("remote.error.noMighty"))
        }
        let wanted = request.styleId ?? request.mightyStyle
        if let wanted, mobileStyleId(session) != wanted,
           !(session.kind == "claude" && session.provider == "claude") {
            throw MobileHostError.conflict(L("remote.error.styleUnavailable"))
        }
        if let mode = request.permissionMode, mode != session.settings.permissionMode,
           !permissionModes(for: session).contains(mode) {
            throw MobileHostError.conflict(L("remote.error.permissionUnverified"))
        }
    }

    private func mobileApplyViewMode(_ id: String, mode: String) throws {
        guard let session = mobilePane(id) else { throw MobileHostError.notFound(L("remote.error.paneNotFound")) }
        guard mobileViewMode(session) != mode else { return }
        setAgentViewMode(id, mode: mode == MobileWire.plainViewMode ? "default" : "mighty")
        guard let updated = mobilePane(id), mobileViewMode(updated) == mode else {
            throw MobileHostError.conflict(L("remote.error.noMighty"))
        }
    }

    /// `shown` is the style id this request's sender was last told, read before
    /// anything in the request was applied. A pane in plain view reports `cli`
    /// whatever it has stored, so a phone echoing that pair back while turning
    /// Mighty view on must not be read as "throw the stored style away".
    private func mobileApplyStyleId(_ id: String, styleId: String, shown: String) throws {
        guard styleId != shown else { return }
        guard let session = mobilePane(id) else { throw MobileHostError.notFound(L("remote.error.paneNotFound")) }
        guard mobileStyleId(session) != styleId else { return }
        setMightyStyle(id, style: styleId == MobileWire.cliStyle ? nil : styleId)
        guard let updated = mobilePane(id), mobileStyleId(updated) == styleId else {
            throw MobileHostError.conflict(L("remote.error.styleUnavailable"))
        }
    }

    private func mobileApplyRunSettings(_ id: String, effort: String?, permissionMode: String?) throws {
        guard effort != nil || permissionMode != nil, let session = mobilePane(id) else { return }
        var settings = session.settings
        if let effort { settings.effort = effort }
        if let permissionMode { settings.permissionMode = permissionMode }
        guard settings != session.settings else { return }
        let saved = mobileCapturingError { saveSettings(id, settings: settings) }
        let applied = mobilePane(id)?.settings
        guard applied?.effort == settings.effort, applied?.permissionMode == settings.permissionMode else {
            throw MobileHostError.conflict(saved.failure ?? L("remote.error.settingsFailed"))
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
        case "usage": return MobileUsageText.text(usage: mobileUsage(session), model: modelLabel(for: session), elapsedSeconds: session.runTiming?.elapsed())
        case "clear":
            guard !hasBlockingModal else { throw MobileHostError.conflict(L("remote.error.closeMacWindow")) }
            guard session.status != "running", !pendingRuns.contains(id) else { throw MobileHostError.conflict(L("slash.note.newConversationRunning")) }
            guard session.resumeId != nil else { throw MobileHostError.conflict(L("remote.error.nothingToResume")) }
            // Not `performSlashAction`: that selects the pane, so a phone would
            // move the Mac user's focus. The checks above are its guards.
            resetConversation(id)
            return nil
        default: throw MobileHostError.badRequest(L("remote.error.unsupportedCommand"))
        }
    }

    /// Stop, permission and question routes act on an existing, non-terminal pane.
    func mobileValidateCommand(_ id: String) throws {
        guard !ending, !closingSessions.contains(id), let session = mobilePane(id) else { throw MightyError(L("remote.error.paneNotFound")) }
        guard !usesLocalTerminal(session) else { throw MightyError(L("remote.error.localTerminalControl")) }
    }

    /// The same test `stop` applies before it does anything: a run in motion,
    /// or one still being started.
    func mobileHasRun(_ id: String) -> Bool {
        mobilePane(id)?.status == "running" || pendingRuns.contains(id)
    }

    func mobilePendingRequest(sessionId: String, requestId: String, runId: String) throws -> ToolPermissionRequest {
        try mobileValidateCommand(sessionId)
        guard let request = toolPermissions[sessionId]?.first(where: { $0.id == requestId && $0.runId == runId && $0.state == "pending" }) else {
            throw MightyError(L("remote.error.notPendingPermission"))
        }
        return request
    }

    func mobileCheckPermissionOutcome(sessionId: String, requestId: String) throws {
        if toolPermissions[sessionId]?.contains(where: { $0.id == requestId && $0.state == "pending" }) == true {
            throw MightyError(permissionErrors[sessionId] ?? L("remote.error.permissionDelivery"))
        }
    }

    func mobileCreateSession(workspaceId: String, kind: String, provider: String) throws -> String {
        guard snapshot.workspaces.contains(where: { $0.id == workspaceId && $0.remote == nil }) else { throw MobileHostError.notFound(L("remote.error.workspaceNotFound")) }
        // A command pane runs in the app's own terminal, which the phone
        // cannot drive; creating one would hand it a dead window.
        guard kind != "shell" else { throw MobileHostError.conflict(L("remote.error.shellUnavailable")) }
        guard !hasBlockingModal else { throw MightyError(L("remote.error.closeMacWindow")) }
        let created = mobileCapturingError { addSession(kind: kind, provider: provider, workspaceId: workspaceId) }
        guard let id = created.value else { throw MightyError(created.failure ?? L("remote.error.createFailed")) }
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
