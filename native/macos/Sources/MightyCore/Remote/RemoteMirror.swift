import CryptoKit
import Foundation

// The remote mirror (docs/remote-workspaces.md "그 컴퓨터의 창 그대로"): a client connected over Tailscale sees the host's
// shared workspaces with the agent panes already open there — their conversations, diagrams, progress and status —
// and keeps using them from here. The panes stay the host's: a send from the client is the host pane's next request,
// as the phone's is. The wire below is versioned on its own (`RemoteMirrorProtocol.version`) inside protocol 2.

public enum RemoteMirrorProtocol {
    /// Sent as `mirror` in every mirror answer; a client refuses another.
    public static let version = 1
    /// The newest transcript entries a pane's detail carries (the phone's page, `MobileSessionDetail.maximumEntries`).
    public static let maximumEntries = MobileSessionDetail.maximumEntries
    /// The newest graph runs a pane's detail carries (the phone's, `MobileWire.mightyRuns`).
    public static let maximumGraphRuns = MobileWire.mightyRuns
    /// One detail answer stays under this many bytes (a client reads at most `RemoteProtocol.maximumResponseBytes`).
    public static let maximumDetailBytes = RemoteProtocol.maximumPollBytes
    /// Panes listed per host.
    public static let maximumSessions = 128
    /// Queued requests listed per pane, each cut to `maximumQueuedText` characters.
    public static let maximumQueued = 16
    public static let maximumQueuedText = 500
    /// Pending tool requests a pane's detail carries.
    public static let maximumPermissions = RemoteProtocol.maximumPendingPermissions
    /// How often a client reads the host's list: while one of its panes runs or the user looks at one, and otherwise.
    public static let livePollInterval: TimeInterval = 1
    public static let idlePollInterval: TimeInterval = 5
}

/// One host pane in the list (`GET /v2/mirror`): enough for the sidebar, and a revision that moves whenever anything the
/// pane's detail carries changed.
public struct RemoteMirrorSummary: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var workspaceId: String
    public var title: String
    public var kind: String
    public var provider: String
    public var model: String
    /// `idle`, `running`, `completed`, `error` or `stopped`.
    public var status: String
    public var revision: String
    /// Tool, question and plan requests waiting for an answer.
    public var pending: Int
    public var queued: Int
    /// The pane's own worktree branch (`PaneWorktree`), when it works in one.
    public var branch: String?
    public init(id: String, workspaceId: String, title: String, kind: String, provider: String, model: String, status: String, revision: String,
                pending: Int = 0, queued: Int = 0, branch: String? = nil) {
        self.id = id; self.workspaceId = workspaceId; self.title = title; self.kind = kind; self.provider = provider; self.model = model
        self.status = status; self.revision = revision; self.pending = pending; self.queued = queued; self.branch = branch
    }
}

/// A request waiting in a host pane's queue.
public struct RemoteMirrorQueued: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var text: String
    public init(id: String, text: String) { self.id = id; self.text = text }
}

/// One host pane in full (`GET /v2/mirror/sessions/{id}`): what the client's pane views draw.
public struct RemoteMirrorSession: Codable, Sendable, Equatable {
    public var id: String
    public var workspaceId: String
    public var title: String
    public var titleMode: String?
    public var kind: String
    public var provider: String
    public var model: String
    public var status: String
    public var createdAt: String
    public var settings: RunSettings
    public var agentViewMode: String?
    public var mightyStyle: String?
    /// The newest `RemoteMirrorProtocol.maximumEntries` entries, pictures left out (their cache keys are the host's files).
    public var logs: [LogEntry]
    /// The host holds older entries than `logs`.
    public var hasOlder: Bool
    /// The newest `RemoteMirrorProtocol.maximumGraphRuns` runs, without host paths (task output files, pictures).
    public var graphRuns: [MightyGraphRun]
    public var todoProgress: TodoProgress?
    public var backgroundWork: BackgroundWork?
    /// The current request's clock, ISO 8601 (`startedAt`, `finishedAt` while it is over).
    public var startedAt: String?
    public var finishedAt: String?
    public var permissions: [ToolPermissionRequest]
    public var queued: [RemoteMirrorQueued]
    public var branch: String?
    public var base: String?
    public init(id: String, workspaceId: String, title: String, titleMode: String? = nil, kind: String, provider: String, model: String, status: String,
                createdAt: String, settings: RunSettings, agentViewMode: String? = nil, mightyStyle: String? = nil, logs: [LogEntry] = [], hasOlder: Bool = false,
                graphRuns: [MightyGraphRun] = [], todoProgress: TodoProgress? = nil, backgroundWork: BackgroundWork? = nil, startedAt: String? = nil,
                finishedAt: String? = nil, permissions: [ToolPermissionRequest] = [], queued: [RemoteMirrorQueued] = [], branch: String? = nil, base: String? = nil) {
        self.id = id; self.workspaceId = workspaceId; self.title = title; self.titleMode = titleMode; self.kind = kind; self.provider = provider
        self.model = model; self.status = status; self.createdAt = createdAt; self.settings = settings; self.agentViewMode = agentViewMode
        self.mightyStyle = mightyStyle; self.logs = logs; self.hasOlder = hasOlder; self.graphRuns = graphRuns; self.todoProgress = todoProgress
        self.backgroundWork = backgroundWork; self.startedAt = startedAt; self.finishedAt = finishedAt; self.permissions = permissions
        self.queued = queued; self.branch = branch; self.base = base
    }
}

/// The host's list as the client reads it.
public struct RemoteMirrorState: Sendable, Equatable {
    public var hostName: String
    public var revision: String
    /// The client's `since` was still current: `workspaces` and `sessions` are empty and mean nothing.
    public var unchanged: Bool
    public var workspaces: [RemoteSharedWorkspace]
    public var sessions: [RemoteMirrorSummary]
    public init(hostName: String, revision: String, unchanged: Bool = false, workspaces: [RemoteSharedWorkspace] = [], sessions: [RemoteMirrorSummary] = []) {
        self.hostName = hostName; self.revision = revision; self.unchanged = unchanged; self.workspaces = workspaces; self.sessions = sessions
    }
}

/// What ties a client pane to the host pane it mirrors. Never saved: mirrored panes live only while the app runs.
public struct RemoteMirrorReference: Sendable, Equatable {
    public var connectionId: String
    public var hostSessionId: String
    public var hostWorkspaceId: String
    public var hostName: String
    public init(connectionId: String, hostSessionId: String, hostWorkspaceId: String, hostName: String) {
        self.connectionId = connectionId; self.hostSessionId = hostSessionId; self.hostWorkspaceId = hostWorkspaceId; self.hostName = hostName
    }
}

/// The host side: this app's panes as a connected client reads and drives them. Only panes of shared workspaces are ever
/// asked for; the host service checks that before every call. The app implements it on the main actor.
public protocol RemoteMirrorHosting: AnyObject, Sendable {
    /// Every agent pane of these workspaces, in the order the host lists them.
    func mirrorSummaries(workspaceIds: Set<String>) async -> [RemoteMirrorSummary]
    /// One pane in full with its revision; nil when it is gone or not in these workspaces.
    func mirrorDetail(sessionId: String, workspaceIds: Set<String>) async -> RemoteMirrorPane?
    /// Which workspace an agent pane is in and the permission mode it runs in (full access needs the host's switch); nil when
    /// there is no such agent pane.
    func mirrorPaneInfo(sessionId: String) async -> RemoteMirrorPaneInfo?
    /// A request into the host pane, as the phone's send: started, queued behind its run (the word says which).
    func mirrorSubmit(sessionId: String, text: String, attachments: [RunAttachment]) async throws -> String
    func mirrorStop(sessionId: String) async throws
    func mirrorPermission(sessionId: String, requestId: String, runId: String, allow: Bool, always: Bool) async throws
    func mirrorAnswers(sessionId: String, requestId: String, runId: String, answers: [String: UserQuestionAnswer]) async throws
    func mirrorPlan(sessionId: String, requestId: String, runId: String, decision: PlanDecision) async throws
    /// A new agent pane in a shared workspace (the workspace's worktree mode decides without asking); its id.
    func mirrorCreateSession(workspaceId: String, provider: String) async throws -> String
    func mirrorClose(sessionId: String) async throws
}

/// A pane's detail and the revision it was read at.
public struct RemoteMirrorPane: Sendable, Equatable {
    public var revision: String
    public var session: RemoteMirrorSession
    public init(revision: String, session: RemoteMirrorSession) { self.revision = revision; self.session = session }
}

public struct RemoteMirrorPaneInfo: Sendable, Equatable {
    public var workspaceId: String
    public var permissionMode: String
    public init(workspaceId: String, permissionMode: String) { self.workspaceId = workspaceId; self.permissionMode = permissionMode }
}

// MARK: - Wire

struct RemoteWireMirrorState: Codable, Sendable {
    var `protocol` = RemoteProtocol.version
    var mirror = RemoteMirrorProtocol.version
    var hostName: String
    /// Moves whenever the list (workspaces, panes, any pane's revision) changed.
    var revision: String
    /// The client's `since` was this revision: nothing else is sent.
    var unchanged: Bool?
    var workspaces: [RemoteSharedWorkspace]
    var sessions: [RemoteMirrorSummary]
}

struct RemoteWireMirrorDetail: Codable, Sendable {
    var `protocol` = RemoteProtocol.version
    var mirror = RemoteMirrorProtocol.version
    var revision: String
    var session: RemoteMirrorSession
}

/// `attachments` may be left out (no files).
struct RemoteWireMirrorSubmit: Codable, Sendable {
    var text: String
    var attachments: [RunAttachment] = []
    init(text: String, attachments: [RunAttachment] = []) { self.text = text; self.attachments = attachments }
    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        text = try container.decode(String.self, forKey: .text)
        attachments = try container.decodeIfPresent([RunAttachment].self, forKey: .attachments) ?? []
    }
}
struct RemoteWireMirrorOutcome: Codable, Sendable { var `protocol` = RemoteProtocol.version; var outcome: String }
struct RemoteWireMirrorPermission: Codable, Sendable { var requestId: String; var runId: String; var allow: Bool; var always: Bool? }
struct RemoteWireMirrorQuestions: Codable, Sendable { var requestId: String; var runId: String; var answers: [String: UserQuestionAnswer] }
struct RemoteWireMirrorCreate: Codable, Sendable { var provider: String }
struct RemoteWireMirrorCreated: Codable, Sendable { var `protocol` = RemoteProtocol.version; var sessionId: String }

// MARK: - Rules both sides read

public enum RemoteMirrorSupport {
    /// A stable id on the client for a host's workspace or pane: `mirror-` and 32 hex digits of SHA-256 over the connection
    /// and the host's id, so two connections never collide and the same host pane keeps one client id while connected.
    public static func clientId(connectionId: String, hostId: String) -> String {
        let digest = SHA256.hash(data: Data((connectionId + "\u{0}" + hostId).utf8))
        return "mirror-" + digest.prefix(16).map { String(format: "%02x", $0) }.joined()
    }

    /// A pane's revision: everything its detail carries that can change, read cheaply (the transcript's tail, not all of it;
    /// the graph's block identities and states, not their streamed text). Equal on the same host process for the same pane.
    public static func revision(of session: RunSession, permissions: [ToolPermissionRequest], queued: [QueuedInput], branch: String?) -> String {
        var parts: [String] = [session.status, session.title, session.provider, session.model, session.settings.permissionMode, session.settings.effort,
                               session.agentViewMode ?? "", session.mightyStyle ?? "", String(session.logs.count), branch ?? ""]
        parts += session.logs.suffix(12).map { "\($0.id):\($0.text.utf8.count):\($0.activity?.state ?? "")" }
        parts.append(String(MobileMightySupport.digest(session: session)))
        if let todos = session.todoProgress { parts += todos.items.map { ($0.id ?? $0.content) + ":" + $0.status } }
        if let work = session.backgroundWork { parts += work.tasks.map { $0.id + ":" + $0.status } }
        parts.append(session.runTiming.map { "\($0.startedAt.timeIntervalSince1970):\($0.finishedAt?.timeIntervalSince1970 ?? 0)" } ?? "")
        parts += permissions.map { $0.id + ":" + $0.runId + ":" + $0.state }
        parts += queued.map(\.id)
        return digest(parts)
    }

    /// The list's revision: its workspaces and every pane's id and revision, in order.
    public static func stateRevision(workspaces: [RemoteSharedWorkspace], sessions: [RemoteMirrorSummary]) -> String {
        digest(workspaces.map { $0.id + ":" + $0.name } + sessions.map { [$0.id, $0.revision, $0.title, $0.status, String($0.pending), String($0.queued)].joined(separator: ":") })
    }

    static func digest(_ parts: [String]) -> String {
        SHA256.hash(data: Data(parts.joined(separator: "\u{1}").utf8)).prefix(12).map { String(format: "%02x", $0) }.joined()
    }

    /// The host pane as the wire carries it, bounded like the phone's payload and under `maximumDetailBytes`.
    public static func session(_ session: RunSession, permissions: [ToolPermissionRequest], queued: [QueuedInput], branch: String? = nil, base: String? = nil) -> RemoteMirrorSession {
        let logs = MobileRemoteSupport.phoneEntries(Array(session.logs.suffix(RemoteMirrorProtocol.maximumEntries)))
        let runs = (session.graphRuns ?? []).suffix(RemoteMirrorProtocol.maximumGraphRuns).map(withoutHostPaths)
        let formatter = ISO8601DateFormatter()
        let value = RemoteMirrorSession(
            id: session.id, workspaceId: session.workspaceId, title: String(session.title.prefix(200)), titleMode: session.titleMode, kind: session.kind,
            provider: session.provider, model: session.model, status: session.status, createdAt: session.createdAt, settings: session.settings,
            agentViewMode: session.agentViewMode, mightyStyle: session.mightyStyle, logs: logs, hasOlder: session.logs.count > logs.count,
            graphRuns: Array(runs), todoProgress: session.todoProgress, backgroundWork: session.backgroundWork,
            startedAt: session.runTiming.map { formatter.string(from: $0.startedAt) }, finishedAt: session.runTiming?.finishedAt.map { formatter.string(from: $0) },
            permissions: Array(permissions.filter { $0.state == "pending" }.suffix(RemoteMirrorProtocol.maximumPermissions)),
            queued: queued.prefix(RemoteMirrorProtocol.maximumQueued).map { RemoteMirrorQueued(id: $0.id, text: String($0.text.prefix(RemoteMirrorProtocol.maximumQueuedText))) },
            branch: branch, base: base)
        return fitted(value, budget: RemoteMirrorProtocol.maximumDetailBytes)
    }

    /// A graph run without the host's paths: a task's output file and pictures' cache keys name files on the host.
    static func withoutHostPaths(_ run: MightyGraphRun) -> MightyGraphRun {
        var run = run
        run.rootEntries = MobileRemoteSupport.phoneEntries(run.rootEntries)
        run.resultEntries = MobileRemoteSupport.phoneEntries(run.resultEntries)
        for index in run.agents.indices {
            run.agents[index].outputFile = nil
            run.agents[index].entries = MobileRemoteSupport.phoneEntries(run.agents[index].entries)
        }
        return run
    }

    /// `value` cut until its JSON is at most `budget` bytes: the oldest graph runs first (keeping one), then the oldest
    /// entries (keeping one), and last each remaining text to 32 KiB.
    public static func fitted(_ value: RemoteMirrorSession, budget: Int) -> RemoteMirrorSession {
        let encoder = JSONEncoder()
        func size(_ session: RemoteMirrorSession) -> Int { (try? encoder.encode(session).count) ?? Int.max }
        var session = value
        while size(session) > budget {
            if session.graphRuns.count > 1 { session.graphRuns.removeFirst(); continue }
            if session.logs.count > 1 { session.logs.removeFirst(); session.hasOlder = true; continue }
            let cut = 32 * 1024
            var changed = false
            for index in session.logs.indices where session.logs[index].text.utf8.count > cut {
                session.logs[index].text = String(session.logs[index].text.utf8.prefix(cut)) ?? ""
                changed = true
            }
            if !session.graphRuns.isEmpty, size(session) > budget { session.graphRuns = []; changed = true }
            if !changed { break }
        }
        return session
    }

    /// A host's list as the client takes it: this version, bounded counts and identifiers, panes only of listed workspaces.
    static func valid(_ state: RemoteWireMirrorState) -> Bool {
        guard state.protocol == RemoteProtocol.version, state.mirror == RemoteMirrorProtocol.version, !state.hostName.isEmpty, state.hostName.count <= 120,
              state.revision.count <= 64 else { return false }
        if state.unchanged == true { return true }
        let workspaces = Set(state.workspaces.map(\.id))
        return state.workspaces.count <= RemoteProtocol.maximumSharedWorkspaces && state.workspaces.allSatisfy(RemoteWorkspaceValidation.workspace)
            && workspaces.count == state.workspaces.count && state.sessions.count <= RemoteMirrorProtocol.maximumSessions
            && Set(state.sessions.map(\.id)).count == state.sessions.count
            && state.sessions.allSatisfy { summary in
                CoreValidation.identifier(summary.id) && workspaces.contains(summary.workspaceId) && summary.kind == SessionKind.claude
                    && ProviderOptions.ids.contains(summary.provider) && summary.title.count <= 200 && summary.model.count <= 200 && summary.revision.count <= 64
                    && ["idle", "running", "completed", "error", "stopped"].contains(summary.status) && (0...10_000).contains(summary.pending)
                    && (0...10_000).contains(summary.queued) && (summary.branch?.count ?? 0) <= 200
            }
    }

    /// One pane's detail as the client takes it.
    static func valid(_ detail: RemoteWireMirrorDetail, sessionId: String) -> Bool {
        let session = detail.session
        guard detail.protocol == RemoteProtocol.version, detail.mirror == RemoteMirrorProtocol.version, detail.revision.count <= 64, session.id == sessionId,
              session.kind == SessionKind.claude, ProviderOptions.ids.contains(session.provider), session.title.count <= 200,
              ["idle", "running", "completed", "error", "stopped"].contains(session.status),
              session.logs.count <= RemoteMirrorProtocol.maximumEntries, session.graphRuns.count <= RemoteMirrorProtocol.maximumGraphRuns,
              session.permissions.count <= RemoteMirrorProtocol.maximumPermissions, session.queued.count <= RemoteMirrorProtocol.maximumQueued else { return false }
        return session.logs.allSatisfy { CoreValidation.identifier($0.id) && LogEntryKinds.stored.contains($0.kind) && $0.text.utf8.count <= 131_072 }
            && session.permissions.allSatisfy { CoreValidation.identifier($0.runId) && !$0.id.isEmpty && $0.id.count <= 256 && $0.inputJSON.utf8.count <= 65_536 }
    }

    /// The client pane for a host pane: a new one from its summary, or the one it already has with the host's fields laid
    /// over it. What the client keeps of its own (block sizes, view choices made here) stays.
    public static func pane(from summary: RemoteMirrorSummary, existing: RunSession?, clientId: String, workspaceId: String) -> RunSession {
        var session = existing ?? RunSession(id: clientId, workspaceId: workspaceId, title: summary.title, kind: SessionKind.claude, provider: summary.provider)
        session.workspaceId = workspaceId
        session.title = summary.title
        session.provider = summary.provider
        session.model = summary.model
        session.status = summary.status
        return session
    }

    /// The host pane's detail laid over the client pane.
    public static func apply(_ detail: RemoteMirrorSession, to existing: RunSession) -> RunSession {
        var session = existing
        session.title = detail.title
        session.titleMode = detail.titleMode
        session.provider = detail.provider
        session.model = detail.model
        session.status = detail.status
        session.settings = detail.settings
        if let mode = detail.agentViewMode { session.agentViewMode = mode }
        session.mightyStyle = detail.mightyStyle
        session.logs = detail.logs
        session.graphRuns = detail.graphRuns
        session.todoProgress = detail.todoProgress
        session.backgroundWork = detail.backgroundWork
        let formatter = ISO8601DateFormatter()
        if let started = detail.startedAt.flatMap(formatter.date(from:)) {
            session.runTiming = AgentRunTiming(startedAt: started, finishedAt: detail.finishedAt.flatMap(formatter.date(from:)))
        } else { session.runTiming = nil }
        return session
    }
}

extension AppSnapshot {
    /// The snapshot as it is saved: without the remote mirror's workspaces, their panes and what refers to them (they live
    /// only while connected, and the host's transcripts never land in this app's state file).
    public func withoutMirrors() -> AppSnapshot {
        let mirrored = Set(workspaces.filter { $0.remote?.mirror == true }.map(\.id))
        guard !mirrored.isEmpty || sessions.contains(where: { $0.mirror != nil }) else { return self }
        var value = self
        value.workspaces.removeAll { mirrored.contains($0.id) }
        let gone = Set(sessions.filter { $0.mirror != nil || mirrored.contains($0.workspaceId) }.map(\.id))
        value.sessions.removeAll { gone.contains($0.id) }
        for id in mirrored {
            value.paneLayouts?.removeValue(forKey: id); value.paneLayoutModes?.removeValue(forKey: id); value.paneLayoutActiveSessionIds?.removeValue(forKey: id)
        }
        value.expandedWorkspaceIds?.removeAll { mirrored.contains($0) }
        if let active = value.activeWorkspaceId, mirrored.contains(active) { value.activeWorkspaceId = value.workspaces.first?.id }
        if let active = value.activeSessionId, gone.contains(active) { value.activeSessionId = nil }
        return value
    }
}

/// The mirror's paths under `/v2/`.
enum RemoteMirrorRoutes {
    static let prefix = RemoteProtocol.pathPrefix + "mirror"
    /// `POST /v2/mirror/sessions/{id}/submit`: the one mirror request that may carry attachments.
    static func isSubmit(_ target: String) -> Bool {
        let parts = target.split(separator: "/", omittingEmptySubsequences: false)
        return parts.count == 6 && parts[0].isEmpty && parts[1] == "v2" && parts[2] == "mirror" && parts[3] == "sessions" && parts[5] == "submit"
            && CoreValidation.identifier(String(parts[4]))
    }
}
