import Foundation
import CryptoKit

// Remote workspaces: one Mighty Claude opens a workspace another computer's
// Mighty Claude shares over Tailscale and runs agents and commands there
// (docs/remote-workspaces.md). The host is `RemoteHostService`, the client
// `RemoteClientService`; this file holds what both read.

/// The app-to-app protocol: authenticated HTTP requests and per-run output cursors.
public enum RemoteProtocol {
    /// Sent and required as `x-mighty-remote-version`. The remote workspaces removed on
    /// 2026-09-28 spoke version 1 under `/v1/`; neither side answers the other.
    public static let version = 2
    public static let header = "x-mighty-remote-version"
    public static let pathPrefix = "/v2/"
    public static let defaultPort = 43137
    /// A run the client has not polled for this long is stopped on the host.
    public static let heartbeatTimeout: TimeInterval = 30
    /// Up to this many workspaces are shared at once.
    public static let maximumSharedWorkspaces = 64
    /// Up to this many remote runs go at once, per host and per client.
    public static let maximumRuns = 16
    /// Saved connections on a client.
    public static let maximumConnections = 16
    /// A request without attachments; a run request with them may reach `AttachmentSupport.maximumRequestBytes`.
    public static let maximumRequestBytes = 512 * 1024
    /// Any response the client reads.
    public static let maximumResponseBytes = 2 * 1024 * 1024
    /// One poll answers with at most this much of a run's events…
    public static let maximumPollBytes = 1_536 * 1024
    public static let maximumPollEvents = 256
    /// …and the host keeps at most this much of each run's events for the client to read.
    public static let maximumBufferedBytes = 4 * 1024 * 1024
    public static let maximumBufferedEvents = 4096
    /// How long one connection may take, request and answer together.
    public static let requestTimeout: TimeInterval = 15
    /// A run request with attachments.
    public static let uploadTimeout: TimeInterval = 60
}

/// A workspace the host shares, as the client lists it.
public struct RemoteSharedWorkspace: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var name: String
    public var path: String
    public init(id: String, name: String, path: String) { self.id = id; self.name = name; self.path = path }
}

/// What "이 컴퓨터 공유" shows. Sharing is always off when the app starts.
public struct RemoteHostState: Sendable, Equatable {
    public var enabled = false
    /// Where clients reach this computer, `address:port`, one per Tailscale address.
    public var addresses: [String] = []
    public var port: Int?
    /// The connection key while sharing; stopping revokes it for good.
    public var key: String?
    public var workspaceIds: [String] = []
    public var activeRuns = 0
    public init() {}
}

/// One saved "다른 컴퓨터 연결" entry. Its key is in the Keychain, never here.
public struct RemoteConnectionInfo: Sendable, Equatable, Identifiable {
    public enum Status: String, Sendable, Equatable {
        /// Saved, not connected since the app started: the key is read from the Keychain on first use.
        case saved
        case connected
        /// Disconnected by the user or by a network error (`detail` says which).
        case disconnected
    }
    public var id: String
    public var name: String
    /// `host:port`, as `RemoteAddress.parse` wrote it.
    public var address: String
    public var status: Status
    public var hostName: String?
    public var workspaces: [RemoteSharedWorkspace]
    /// The host's CLIs, models and what they support, from its last answer.
    public var runtime: RuntimeInfo?
    public var detail: String?
    public init(id: String = UUID().uuidString, name: String, address: String, status: Status = .saved, hostName: String? = nil, workspaces: [RemoteSharedWorkspace] = [], runtime: RuntimeInfo? = nil, detail: String? = nil) {
        self.id = id; self.name = name; self.address = address; self.status = status; self.hostName = hostName; self.workspaces = workspaces; self.runtime = runtime; self.detail = detail
    }
}

/// Remote workspaces are not mirrored to the phone: the phone sees this Mac's
/// own workspaces and panes only, never another computer's.
public enum RemoteWorkspacePhone {
    public static func workspaces(_ all: [Workspace]) -> [Workspace] { all.filter { $0.remote == nil } }
    public static func sessions(_ sessions: [RunSession], workspaces: [Workspace]) -> [RunSession] {
        let local = Set(workspaces.filter { $0.remote == nil }.map(\.id))
        return sessions.filter { local.contains($0.workspaceId) }
    }
}

// MARK: - Wire

struct RemoteWireInfo: Codable, Sendable {
    var `protocol` = RemoteProtocol.version
    var hostId: String
    var hostName: String
    var workspaces: [RemoteSharedWorkspace]
    var runtime: RuntimeInfo
}

/// A run request. `StartRunRequest` encodes only what a pane saves; what the
/// app adds per run (a plan-mode style's launch, registered models, a resumed
/// checklist, Settings → Models) rides beside it.
struct RemoteWireStart: Codable, Sendable {
    var request: StartRunRequest
    var permissionModeOverride: String?
    var registeredModels: [RegisteredModelEntry] = []
    var todoProgress: TodoProgress?
    var phaseModels: PhaseModelHardcodedConfig?
}

struct RemoteWireJob: Codable, Sendable { var `protocol` = RemoteProtocol.version; var jobId: String }
struct RemoteWireEvent: Codable, Sendable { var cursor: Int; var event: RunEvent }
struct RemoteWirePoll: Codable, Sendable {
    var `protocol` = RemoteProtocol.version
    /// The last cursor in `events` (the request's when none).
    var cursor: Int
    /// The run's newest cursor; more is waiting while it is past `cursor`.
    var lastCursor: Int
    /// Events after the request's cursor were dropped from the host's buffer.
    var gap: Bool
    var done: Bool
    var events: [RemoteWireEvent]
}
struct RemoteWireSteer: Codable, Sendable { var text: String }
struct RemoteWirePermission: Codable, Sendable { var runId: String; var requestId: String; var allow: Bool }
struct RemoteWireQuestions: Codable, Sendable { var runId: String; var requestId: String; var answers: [String: UserQuestionAnswer] }
struct RemoteWirePlan: Codable, Sendable {
    var runId: String; var requestId: String; var outcome: PlanOutcome; var feedback: String?
    init(runId: String, requestId: String, decision: PlanDecision) {
        self.runId = runId; self.requestId = requestId; outcome = decision.outcome
        if case .revise(let text) = decision { feedback = text }
    }
    var decision: PlanDecision? {
        switch outcome {
        case .approvedAuto: return .approveAutoEdit
        case .approvedConfirm: return .approveConfirmEach
        case .revised: return feedback.map { .revise(feedback: $0) }
        case .cancelled: return .cancel
        }
    }
}
struct RemoteWireAnswer: Codable, Sendable { var `protocol` = RemoteProtocol.version; var accepted: Bool }

// MARK: - Validation

enum RemoteWorkspaceValidation {
    /// A connection key: 256 random bits, base64url (43 characters) or longer.
    static func key(_ value: String) -> Bool { value.range(of: "^[A-Za-z0-9_-]{43,128}$", options: .regularExpression) != nil }

    /// Constant-time: both sides are hashed first, so neither the length nor the
    /// first differing byte shows in the time taken.
    static func keysMatch(_ presented: String, _ expected: String) -> Bool {
        let lhs = Array(SHA256.hash(data: Data(presented.utf8))), rhs = Array(SHA256.hash(data: Data(expected.utf8)))
        var difference: UInt8 = 0
        for index in lhs.indices { difference |= lhs[index] ^ rhs[index] }
        return difference == 0
    }

    static func workspace(_ value: RemoteSharedWorkspace) -> Bool {
        CoreValidation.identifier(value.id) && !value.name.isEmpty && value.name.count <= 120
            && !value.name.unicodeScalars.contains { $0.properties.generalCategory == .control }
            && StateRepository.absolutePath(value.path, remote: true)
    }

    static func info(_ value: RemoteWireInfo) throws {
        guard value.protocol == RemoteProtocol.version, CoreValidation.identifier(value.hostId), !value.hostName.isEmpty, value.hostName.count <= 120,
              value.workspaces.count <= RemoteProtocol.maximumSharedWorkspaces, value.workspaces.allSatisfy(workspace),
              Set(value.workspaces.map(\.id)).count == value.workspaces.count,
              ["darwin", "win32", "linux"].contains(value.runtime.platform), value.runtime.appVersion.count <= 80,
              (value.runtime.providers?.count ?? 0) <= ProviderOptions.ids.count else {
            throw MightyError(L("remote.connect.error.notMighty"))
        }
        for provider in value.runtime.providers ?? [] {
            guard ProviderOptions.ids.contains(provider.id), provider.name.count <= 120, provider.detail.count <= 2_000, (provider.version?.count ?? 0) <= 80,
                  provider.modelCatalog.models.count <= 128, provider.modelCatalog.detail.count <= 2_000,
                  provider.modelCatalog.models.allSatisfy({ CoreValidation.model($0.value) && $0.displayName.count <= 160 && $0.description.count <= 2_400 }),
                  provider.capabilities.permissionModes.allSatisfy({ permissionMode($0, provider: provider.id) }) else {
                throw MightyError(L("remote.connect.error.badRuntime"))
            }
        }
    }

    static func permissionMode(_ mode: String, provider: String) -> Bool {
        ["manual", "plan", "acceptEdits", "fullAccess"].contains(mode) || (mode == "auto" && provider == "claude") || (mode == "onRequest" && provider == "codex")
    }

    static let eventTypes: Set<String> = ["log", "status", "resume", "activity", "usage", "graph", "permission", "plan", "todos", "background"]

    /// One event of the host's run, before the client applies it as its own pane's.
    static func event(_ value: RunEvent, sessionId: String) -> Bool {
        guard value.sessionId == sessionId, eventTypes.contains(value.type) else { return false }
        if let activity = value.activity, !ActivitySupport.valid(activity) { return false }
        switch value.type {
        case "status": return ["idle", "running", "completed", "error", "stopped"].contains(value.status ?? "") && (value.reason == nil || value.reason == "auth")
        case "resume": return CoreValidation.identifier(value.resumeId ?? "")
        case "activity": return value.activity != nil
        case "permission":
            guard let permission = value.permission else { return false }
            return CoreValidation.identifier(permission.runId) && !permission.id.isEmpty && permission.id.count <= 256 && permission.toolName.count <= 256
                && permission.inputJSON.utf8.count <= 65_536 && permission.summary.utf8.count <= 16_384 && !permission.state.isEmpty && permission.state.count <= 32
        case "plan": return value.plan != nil
        case "todos": return value.todos != nil
        case "background": return value.background != nil
        case "log":
            guard let entry = value.entry else { return false }
            if let activity = entry.activity, !ActivitySupport.valid(activity) { return false }
            return CoreValidation.identifier(entry.id) && LogEntryKinds.stored.contains(entry.kind) && entry.text.utf8.count <= 131_072
                && (entry.provider == nil || ProviderOptions.ids.contains(entry.provider!))
        // Usage and graph observations are normalized by the client, or left out.
        default: return true
        }
    }

    /// Settings → Models as the client sent it: every value a model name or "default", every effort a known one.
    static func phaseModels(_ value: PhaseModelHardcodedConfig) -> Bool {
        let models = [value.claudeMain, value.claudeOpusAlias, value.claudeSonnetAlias, value.claudeHaikuAlias, value.claudeSubagentDefault, value.codexReviewModel, value.codexSubagentDefault]
        let efforts = [value.codexPlanModeReasoningEffort, value.claudeMainEffort ?? "default", value.codexMainEffort ?? "default", value.codexSubagentEffort ?? "default"]
        return models.allSatisfy { $0 == "default" || CoreValidation.model($0) } && efforts.allSatisfy { $0 == "default" || ProviderOptions.efforts.contains($0) }
    }
}
