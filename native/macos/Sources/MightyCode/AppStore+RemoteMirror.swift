import Foundation
import MightyCore

/// How one connection's mirror goes: reading the host's list, retrying after a failed read, or stopped.
struct RemoteMirrorLink: Equatable {
    enum Phase: Equatable { case live, reconnecting, off }
    var phase: Phase
    /// The host list's revision this Mac holds (`GET /v2/mirror?since=`); nil reads it whole.
    var revision: String?
    var error: String?
    /// The host answered the mirror's list 404: an older Mighty Code (or one without the mirror yet). Its shared workspaces
    /// still open as this Mac's own remote panes (Settings → 원격 연결's "열기").
    var unsupported = false
}

/// What a mirrored pane shows beyond its RunSession: the host's waiting work and branch from its list, the queued texts and
/// the base branch from its detail.
struct RemoteMirrorCounts: Equatable {
    var pending = 0
    var queued = 0
    var queuedTexts: [String] = []
    var branch: String?
    var base: String?
}

/// One server in the sidebar: a saved connection (or one a remote workspace still names) and its workspaces.
struct RemoteServerGroup: Identifiable, Equatable {
    var id: String
    var name: String
    var address: String?
    var connection: RemoteConnectionInfo?
    var link: RemoteMirrorLink?
    var workspaces: [Workspace]
}

/// The host side of the remote mirror: this Mac's panes as a connected client reads and drives them, the reads on the main
/// actor and the actions through the phone's own handlers, so a client's send, stop or answer behaves as the phone's.
final class RemoteMirrorBridge: RemoteMirrorHosting, @unchecked Sendable {
    private weak var store: AppStore?
    private let phone: MobileRemoteBridge
    init(store: AppStore) { self.store = store; phone = MobileRemoteBridge(store: store) }

    func mirrorSummaries(workspaceIds: Set<String>) async -> [RemoteMirrorSummary] {
        await MainActor.run { store?.mirrorSummaries(workspaceIds) ?? [] }
    }
    func mirrorDetail(sessionId: String, workspaceIds: Set<String>) async -> RemoteMirrorPane? {
        await MainActor.run { store?.mirrorPane(sessionId, workspaceIds: workspaceIds) }
    }
    func mirrorPaneInfo(sessionId: String) async -> RemoteMirrorPaneInfo? {
        await MainActor.run { store?.mirrorPaneInfo(sessionId) }
    }
    func mirrorSubmit(sessionId: String, text: String, attachments: [RunAttachment]) async throws -> String {
        try await phone.mobileSubmit(sessionId: sessionId, text: text, mode: nil, attachments: attachments)
    }
    func mirrorStop(sessionId: String) async throws { _ = try await phone.mobileStop(sessionId: sessionId) }
    func mirrorPermission(sessionId: String, requestId: String, runId: String, allow: Bool, always: Bool) async throws {
        try await phone.mobilePermission(sessionId: sessionId, requestId: requestId, runId: runId, allow: allow, always: always)
    }
    func mirrorAnswers(sessionId: String, requestId: String, runId: String, answers: [String: UserQuestionAnswer]) async throws {
        try await phone.mobileAnswers(sessionId: sessionId, requestId: requestId, runId: runId, answers: answers)
    }
    func mirrorPlan(sessionId: String, requestId: String, runId: String, decision: PlanDecision) async throws {
        try await phone.mobilePlan(sessionId: sessionId, requestId: requestId, runId: runId, decision: decision)
    }
    func mirrorCreateSession(workspaceId: String, provider: String) async throws -> String {
        try await phone.mobileCreateSession(workspaceId: workspaceId, kind: SessionKind.claude, provider: provider)
    }
    func mirrorClose(sessionId: String) async throws { try await phone.mobileClose(sessionId: sessionId) }
}

extension AppStore {
    // MARK: Host: this Mac's panes for a connected client

    /// The statuses a client takes; anything else (a word a newer build may add) reads as idle.
    private static let mirrorStatuses: Set<String> = ["idle", "running", "completed", "error", "stopped"]

    /// This Mac's own agent panes the remote mirror may show: of these workspaces, never another computer's.
    private func mirrorable(_ session: RunSession, in workspaceIds: Set<String>) -> Bool {
        workspaceIds.contains(session.workspaceId) && session.kind == SessionKind.claude && session.mirror == nil && !isRemoteWorkspace(session.workspaceId)
    }

    private func mirrorPermissions(_ id: String) -> [ToolPermissionRequest] { (toolPermissions[id] ?? []).filter { $0.state == "pending" } }

    func mirrorSummaries(_ workspaceIds: Set<String>) -> [RemoteMirrorSummary] {
        snapshot.sessions.filter { mirrorable($0, in: workspaceIds) }.map { session in
            let permissions = mirrorPermissions(session.id)
            let queued = queuedInputs[session.id] ?? []
            let branch = session.paneWorktree?.branch
            return RemoteMirrorSummary(id: session.id, workspaceId: session.workspaceId, title: String(session.title.prefix(200)), kind: session.kind,
                                       provider: session.provider, model: String(session.model.prefix(200)),
                                       status: Self.mirrorStatuses.contains(session.status) ? session.status : "idle",
                                       revision: RemoteMirrorSupport.revision(of: session, permissions: permissions, queued: queued, branch: branch),
                                       pending: permissions.count + (antigravityBoard[session.id] == nil ? 0 : 1), queued: queued.count, branch: branch)
        }
    }

    func mirrorPane(_ id: String, workspaceIds: Set<String>) -> RemoteMirrorPane? {
        guard let session = snapshot.sessions.first(where: { $0.id == id }), mirrorable(session, in: workspaceIds) else { return nil }
        let permissions = mirrorPermissions(id)
        let queued = queuedInputs[id] ?? []
        var detail = RemoteMirrorSupport.session(session, permissions: permissions, queued: queued, branch: session.paneWorktree?.branch, base: session.paneWorktree?.baseBranch)
        if !Self.mirrorStatuses.contains(detail.status) { detail.status = "idle" }
        return RemoteMirrorPane(revision: RemoteMirrorSupport.revision(of: session, permissions: permissions, queued: queued, branch: session.paneWorktree?.branch), session: detail)
    }

    func mirrorPaneInfo(_ id: String) -> RemoteMirrorPaneInfo? {
        guard let session = snapshot.sessions.first(where: { $0.id == id }), session.kind == SessionKind.claude, session.mirror == nil,
              !isRemoteWorkspace(session.workspaceId) else { return nil }
        return RemoteMirrorPaneInfo(workspaceId: session.workspaceId, permissionMode: session.settings.permissionMode)
    }

    // MARK: Client: the connected computers' panes here

    func mirrorReference(_ id: String) -> RemoteMirrorReference? { snapshot.sessions.first { $0.id == id }?.mirror }

    /// This Mac's own panes, the ones the 128-pane limit (and the state file) counts; mirrored panes are the host's.
    var ownSessionCount: Int { snapshot.sessions.reduce(0) { $0 + ($1.mirror == nil ? 1 : 0) } }

    /// The servers the sidebar lists under this Mac's own workspaces: every saved connection, then any connection a remote
    /// workspace still names after it was removed. Each with the workspaces the search leaves.
    var remoteServerGroups: [RemoteServerGroup] {
        let remote = filteredWorkspaces.filter { $0.remote != nil }
        var groups = remoteConnections.map { connection in
            RemoteServerGroup(id: connection.id, name: connection.name, address: connection.address, connection: connection, link: remoteMirrorLinks[connection.id],
                              workspaces: remote.filter { $0.remote?.connectionId == connection.id })
        }
        let known = Set(remoteConnections.map(\.id))
        for workspace in remote where !known.contains(workspace.remote!.connectionId) {
            let id = workspace.remote!.connectionId
            if let index = groups.firstIndex(where: { $0.id == id }) { groups[index].workspaces.append(workspace) }
            else { groups.append(RemoteServerGroup(id: id, name: workspace.remote!.hostName, workspaces: [workspace])) }
        }
        // While searching, a server with nothing matching steps aside.
        return search.isEmpty ? groups : groups.filter { !$0.workspaces.isEmpty }
    }

    /// Starts (or wakes) the connection's mirror: its workspaces and panes come into the sidebar and stay current.
    func startMirror(_ connectionId: String) {
        guard !ending else { return }
        if remoteMirrorTasks[connectionId] != nil { wakeMirror(connectionId: connectionId); return }
        remoteMirrorLinks[connectionId] = RemoteMirrorLink(phase: .live, revision: nil)
        remoteMirrorTasks[connectionId] = Task { [weak self] in await self?.mirrorLoop(connectionId) }
    }

    /// Stops reading the connection's host. Its panes stay listed as they were (`removing` takes them away too).
    func stopMirror(_ connectionId: String, removing: Bool = false) {
        remoteMirrorTasks.removeValue(forKey: connectionId)?.cancel()
        remoteMirrorLinks[connectionId]?.phase = .off
        remoteMirrorLinks[connectionId]?.revision = nil
        if removing { remoteMirrorLinks.removeValue(forKey: connectionId); removeMirrors(connectionId) }
    }

    func wakeMirror(connectionId: String) { remoteMirrorWake.insert(connectionId) }
    func wakeMirror(workspaceId: String) {
        guard let remote = snapshot.workspaces.first(where: { $0.id == workspaceId })?.remote, remote.mirror == true else { return }
        wakeMirror(connectionId: remote.connectionId)
    }

    private func mirrorLoop(_ id: String) async {
        var failingSince: Date?
        while !Task.isCancelled, !ending {
            do {
                let state = try await remoteClient.mirrorState(connectionId: id, since: remoteMirrorLinks[id]?.revision)
                guard !Task.isCancelled else { break }
                failingSince = nil
                if !state.unchanged { applyMirrorState(id, state) }
                remoteMirrorLinks[id] = RemoteMirrorLink(phase: .live, revision: state.revision)
                try await fetchMirrorDetails(id)
            } catch {
                if Task.isCancelled { break }
                // A host that answers but refuses the mirror (an older Mighty Code answers 404) stays connected: its
                // workspaces still take this Mac's own remote panes, only the mirror stops.
                if let status = RemoteClientService.refusalStatus(error), status != 401 {
                    let host = remoteConnections.first { $0.id == id }?.name ?? ""
                    remoteMirrorLinks[id] = RemoteMirrorLink(phase: .off, revision: nil,
                                                             error: status == 404 ? L("remote.mirror.error.olderHost", ["host": host]) : error.localizedDescription,
                                                             unsupported: status == 404)
                    break
                }
                // A busy host or a stumbling network is asked again for ten seconds, as a remote run's poll is.
                if RemoteClientService.transient(error), Date().timeIntervalSince(failingSince ?? Date()) < RemoteProtocol.pollRetryWindow {
                    failingSince = failingSince ?? Date()
                    remoteMirrorLinks[id]?.phase = .reconnecting
                    try? await Task.sleep(for: .seconds(1))
                    continue
                }
                await remoteClient.mirrorLost(connectionId: id, message: error.localizedDescription)
                remoteMirrorLinks[id] = RemoteMirrorLink(phase: .off, revision: nil, error: error.localizedDescription)
                remoteConnections = await remoteClient.list()
                break
            }
            await mirrorPause(id)
        }
        if remoteMirrorTasks[id] != nil, !Task.isCancelled { remoteMirrorTasks[id] = nil }
    }

    /// One second while a pane of this connection runs or waits, or the user looks at one of its workspaces; five otherwise.
    /// A selection, a send or an answer wakes it at once.
    private func mirrorPause(_ id: String) async {
        let mine = snapshot.sessions.filter { $0.mirror?.connectionId == id }
        let watching = snapshot.workspaces.first { $0.id == snapshot.activeWorkspaceId }?.remote.map { $0.connectionId == id && $0.mirror == true } ?? false
        let live = watching || mine.contains { $0.status == "running" || (remoteMirrorCounts[$0.id]?.pending ?? 0) > 0 }
        let seconds = live ? RemoteMirrorProtocol.livePollInterval : RemoteMirrorProtocol.idlePollInterval
        let until = Date().addingTimeInterval(seconds)
        while Date() < until, !Task.isCancelled {
            if remoteMirrorWake.remove(id) != nil { return }
            try? await Task.sleep(for: .milliseconds(100))
        }
    }

    /// The host's list laid over this Mac's: a workspace per shared one, a pane per host pane, the ones gone taken away.
    func applyMirrorState(_ id: String, _ state: RemoteMirrorState) {
        var next = snapshot
        let workspaceIds = Dictionary(uniqueKeysWithValues: state.workspaces.map { ($0.id, RemoteMirrorSupport.clientId(connectionId: id, hostId: $0.id)) })
        let keptWorkspaces = Set(workspaceIds.values)
        let dropped = Set(next.workspaces.filter { $0.remote?.connectionId == id && $0.remote?.mirror == true && !keptWorkspaces.contains($0.id) }.map(\.id))
        next.workspaces.removeAll { dropped.contains($0.id) }
        for shared in state.workspaces {
            let clientId = workspaceIds[shared.id]!
            let reference = RemoteWorkspaceReference(connectionId: id, workspaceId: shared.id, hostName: state.hostName, mirror: true)
            if let index = next.workspaces.firstIndex(where: { $0.id == clientId }) {
                next.workspaces[index].name = shared.name; next.workspaces[index].path = shared.path; next.workspaces[index].remote = reference
            } else {
                next.workspaces.append(Workspace(id: clientId, name: shared.name, path: shared.path, remote: reference))
            }
        }
        var keptSessions = Set<String>()
        for summary in state.sessions {
            guard let workspaceId = workspaceIds[summary.workspaceId] else { continue }
            let clientId = RemoteMirrorSupport.clientId(connectionId: id, hostId: summary.id)
            keptSessions.insert(clientId)
            let index = next.sessions.firstIndex { $0.id == clientId }
            var pane = RemoteMirrorSupport.pane(from: summary, existing: index.map { next.sessions[$0] }, clientId: clientId, workspaceId: workspaceId)
            pane.mirror = RemoteMirrorReference(connectionId: id, hostSessionId: summary.id, hostWorkspaceId: summary.workspaceId, hostName: state.hostName)
            if let index { if next.sessions[index] != pane { next.sessions[index] = pane } } else { next.sessions.append(pane) }
            remoteMirrorSummaryRevisions[clientId] = summary.revision
            var counts = remoteMirrorCounts[clientId] ?? RemoteMirrorCounts()
            counts.pending = summary.pending; counts.queued = summary.queued; counts.branch = summary.branch
            if summary.queued == 0 { counts.queuedTexts = [] }
            if remoteMirrorCounts[clientId] != counts { remoteMirrorCounts[clientId] = counts }
        }
        let gone = next.sessions.filter { $0.mirror?.connectionId == id && !keptSessions.contains($0.id) || dropped.contains($0.workspaceId) }.map(\.id)
        next.sessions.removeAll { gone.contains($0.id) }
        // A workspace the host stopped sharing, or a pane closed there, stops being the one shown.
        if let active = next.activeWorkspaceId, dropped.contains(active) {
            next.activeWorkspaceId = next.workspaces.first { $0.remote == nil }?.id ?? next.workspaces.first?.id
            next.activeSessionId = next.sessions.first { $0.workspaceId == next.activeWorkspaceId }?.id
        } else if let active = next.activeSessionId, gone.contains(active) {
            next.activeSessionId = next.sessions.first { $0.workspaceId == next.activeWorkspaceId }?.id
        }
        if next != snapshot { snapshot = next }
        for clientId in gone { forgetMirrorPane(clientId) }
        for clientId in keptWorkspaces { reconcilePaneLayout(clientId) }
    }

    /// The details this Mac needs now: the panes of the workspace the user looks at and any pane waiting for an answer, each
    /// only when its revision moved since it was read.
    private func fetchMirrorDetails(_ id: String) async throws {
        let watching = snapshot.activeWorkspaceId
        let wanted = snapshot.sessions.filter { session in
            guard session.mirror?.connectionId == id else { return false }
            let needed = session.workspaceId == watching || (remoteMirrorCounts[session.id]?.pending ?? 0) > 0
            return needed && remoteMirrorSummaryRevisions[session.id] != remoteMirrorDetailRevisions[session.id]
        }
        for session in wanted.prefix(16) {
            guard let mirror = session.mirror, !Task.isCancelled else { return }
            let pane: RemoteMirrorPane
            do { pane = try await remoteClient.mirrorDetail(connectionId: id, hostSessionId: mirror.hostSessionId) }
            catch where RemoteClientService.refusalStatus(error).map({ $0 != 401 }) ?? false {
                // Closed on the host between its list and this read: the list is read whole next time, which takes it away.
                remoteMirrorDetailRevisions[session.id] = remoteMirrorSummaryRevisions[session.id]
                remoteMirrorLinks[id]?.revision = nil
                continue
            }
            guard snapshot.sessions.contains(where: { $0.id == session.id }) else { continue }
            updateSession(session.id) { $0 = RemoteMirrorSupport.apply(pane.session, to: $0) }
            let pending = pane.session.permissions.filter { $0.state == "pending" }
            if (toolPermissions[session.id] ?? []) != pending { toolPermissions[session.id] = pending.isEmpty ? nil : pending }
            var counts = remoteMirrorCounts[session.id] ?? RemoteMirrorCounts()
            counts.queued = pane.session.queued.count; counts.queuedTexts = pane.session.queued.map(\.text)
            counts.branch = pane.session.branch; counts.base = pane.session.base
            if remoteMirrorCounts[session.id] != counts { remoteMirrorCounts[session.id] = counts }
            remoteMirrorDetailRevisions[session.id] = pane.revision
        }
    }

    private func forgetMirrorPane(_ id: String) {
        remoteMirrorSummaryRevisions.removeValue(forKey: id); remoteMirrorDetailRevisions.removeValue(forKey: id)
        remoteMirrorCounts.removeValue(forKey: id); toolPermissions.removeValue(forKey: id); permissionErrors.removeValue(forKey: id)
        drafts.removeValue(forKey: id)
    }

    /// The connection's mirrored workspaces and panes go (the connection was removed).
    func removeMirrors(_ connectionId: String) {
        var next = snapshot
        let workspaces = Set(next.workspaces.filter { $0.remote?.connectionId == connectionId && $0.remote?.mirror == true }.map(\.id))
        let sessions = next.sessions.filter { $0.mirror?.connectionId == connectionId || workspaces.contains($0.workspaceId) }.map(\.id)
        next.workspaces.removeAll { workspaces.contains($0.id) }
        next.sessions.removeAll { sessions.contains($0.id) }
        if let active = next.activeWorkspaceId, workspaces.contains(active) { next.activeWorkspaceId = next.workspaces.first?.id; next.activeSessionId = nil }
        if next != snapshot { snapshot = next }
        for id in sessions { forgetMirrorPane(id) }
    }

    /// Why a mirrored pane cannot send now: its computer is not connected.
    func mirrorBlockedReason(_ mirror: RemoteMirrorReference) -> String? {
        remoteConnections.first(where: { $0.id == mirror.connectionId })?.status == .connected ? nil : L("remote.mirror.error.disconnected", ["host": mirror.hostName])
    }

    /// The composer's text and files as the host pane's next request; they come back to the composer when the host refuses.
    func submitMirror(_ id: String, mirror: RemoteMirrorReference) {
        let draft = drafts[id] ?? ""
        let text = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        let attachments = attachmentDrafts[id] ?? []
        guard !text.isEmpty || !attachments.isEmpty else { return }
        if let reason = mirrorBlockedReason(mirror) { error = reason; return }
        drafts[id] = ""
        attachmentDrafts[id] = []
        Task { [weak self] in
            guard let self else { return }
            do {
                _ = try await remoteClient.mirrorSubmit(connectionId: mirror.connectionId, hostSessionId: mirror.hostSessionId, text: text, attachments: attachments)
                wakeMirror(connectionId: mirror.connectionId)
            } catch {
                if (drafts[id] ?? "").isEmpty { drafts[id] = draft }
                if (attachmentDrafts[id] ?? []).isEmpty { attachmentDrafts[id] = attachments }
                self.error = error.localizedDescription
            }
        }
    }

    func stopMirror(_ id: String, mirror: RemoteMirrorReference) async {
        do { try await remoteClient.mirrorStop(connectionId: mirror.connectionId, hostSessionId: mirror.hostSessionId) }
        catch { self.error = error.localizedDescription }
        wakeMirror(connectionId: mirror.connectionId)
    }

    /// A new agent pane on that computer, in the shared workspace; it shows here at the next read and is selected then.
    func createMirrorPane(_ remote: RemoteWorkspaceReference, kind: String, provider: String) {
        guard kind == SessionKind.claude else { error = L("remote.mirror.error.commandPane"); return }
        Task { [weak self] in
            guard let self else { return }
            do {
                let hostId = try await remoteClient.mirrorCreateSession(connectionId: remote.connectionId, hostWorkspaceId: remote.workspaceId, provider: provider)
                remoteMirrorLinks[remote.connectionId]?.revision = nil
                wakeMirror(connectionId: remote.connectionId)
                let clientId = RemoteMirrorSupport.clientId(connectionId: remote.connectionId, hostId: hostId)
                for _ in 0..<50 where !snapshot.sessions.contains(where: { $0.id == clientId }) { try? await Task.sleep(for: .milliseconds(100)) }
                if snapshot.sessions.contains(where: { $0.id == clientId }) { selectSession(clientId) }
            } catch { self.error = error.localizedDescription }
        }
    }

    /// Closes the pane on that computer, as the phone's close does; it leaves here at once.
    func closeMirrorPane(_ id: String, mirror: RemoteMirrorReference) {
        Task { [weak self] in
            guard let self else { return }
            do {
                try await remoteClient.mirrorClose(connectionId: mirror.connectionId, hostSessionId: mirror.hostSessionId)
                var next = snapshot
                next.sessions.removeAll { $0.id == id }
                if next.activeSessionId == id { next.activeSessionId = next.sessions.first { $0.workspaceId == next.activeWorkspaceId }?.id }
                snapshot = next
                forgetMirrorPane(id)
                if let workspace = snapshot.workspaces.first(where: { $0.remote?.connectionId == mirror.connectionId && $0.remote?.workspaceId == mirror.hostWorkspaceId }) {
                    reconcilePaneLayout(workspace.id)
                }
                wakeMirror(connectionId: mirror.connectionId)
            } catch { self.error = error.localizedDescription }
        }
    }

    /// Settings → 원격 연결's "보기": the shared workspace as the mirror lists it. A host that turns out not to have the
    /// mirror opens it as this Mac's own remote workspace instead, as "열기" did.
    func showMirrorWorkspace(connectionId: String, hostWorkspaceId: String) {
        let id = RemoteMirrorSupport.clientId(connectionId: connectionId, hostId: hostWorkspaceId)
        if remoteMirrorLinks[connectionId]?.unsupported == true { openRemoteWorkspace(connectionId: connectionId, hostWorkspaceId: hostWorkspaceId); return }
        startMirror(connectionId)
        showSettings = false
        Task { [weak self] in
            for _ in 0..<50 {
                guard let self else { return }
                if snapshot.workspaces.contains(where: { $0.id == id }) { selectWorkspace(id); return }
                if remoteMirrorLinks[connectionId]?.unsupported == true { openRemoteWorkspace(connectionId: connectionId, hostWorkspaceId: hostWorkspaceId); return }
                try? await Task.sleep(for: .milliseconds(100))
            }
        }
    }
}
