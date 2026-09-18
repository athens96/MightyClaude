import Foundation

/// Wire types of the mobile protocol ("m1"): a phone on the same tailnet
/// reads the desktop's workspaces and panes and sends commands. Kept apart
/// from the desktop-to-desktop `/v1` run protocol, which shares single runs.
/// The contract is documented in docs/mobile-remote.md.
public struct MobileRemoteSettings: Codable, Sendable, Equatable {
    public var enabled: Bool
    /// `wss://host[:port]` (or `ws://` for a local test relay). Empty = not configured.
    public var relayURL: String
    public init(enabled: Bool = false, relayURL: String = "") { self.enabled = enabled; self.relayURL = relayURL }
    enum CodingKeys: String, CodingKey { case enabled, relayURL }
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        enabled = try c.decodeIfPresent(Bool.self, forKey: .enabled) ?? false
        relayURL = try c.decodeIfPresent(String.self, forKey: .relayURL) ?? ""
    }
    public var normalized: MobileRemoteSettings {
        MobileRemoteSettings(enabled: enabled, relayURL: RelayEndpoint.normalize(relayURL) ?? "")
    }
}

public struct MobileInfo: Codable, Sendable, Equatable {
    public var `protocol`: Int = 1
    public var hostId: String
    public var hostName: String
    public var appVersion: String
    public var platform: String
    public init(hostId: String, hostName: String, appVersion: String, platform: String = "darwin") {
        self.hostId = hostId; self.hostName = hostName; self.appVersion = appVersion; self.platform = platform
    }
}

public struct MobileWorkspace: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var name: String
    public var path: String
    public var remote: Bool
    public init(id: String, name: String, path: String, remote: Bool) { self.id = id; self.name = name; self.path = path; self.remote = remote }
}

public struct MobilePreview: Codable, Sendable, Equatable {
    public var kind: String
    public var text: String
    public init(kind: String, text: String) { self.kind = kind; self.text = text }
}

public struct MobileSessionSummary: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var workspaceId: String
    public var title: String
    public var kind: String
    public var provider: String
    public var model: String
    public var status: String
    public var revision: Int
    public var updatedAt: String
    public var preview: MobilePreview?
    public var pendingPermissions: Int
    public var pendingQuestions: Int
    public var queued: Int
    public var resumeId: String?
    public var terminal: Bool
    public init(id: String, workspaceId: String, title: String, kind: String, provider: String, model: String, status: String, revision: Int, updatedAt: String,
                preview: MobilePreview? = nil, pendingPermissions: Int = 0, pendingQuestions: Int = 0, queued: Int = 0, resumeId: String? = nil, terminal: Bool = false) {
        self.id = id; self.workspaceId = workspaceId; self.title = title; self.kind = kind; self.provider = provider; self.model = model; self.status = status
        self.revision = revision; self.updatedAt = updatedAt; self.preview = preview; self.pendingPermissions = pendingPermissions
        self.pendingQuestions = pendingQuestions; self.queued = queued; self.resumeId = resumeId; self.terminal = terminal
    }
}

public struct MobileState: Codable, Sendable, Equatable {
    public var `protocol`: Int = 1
    public var revision: Int
    public var hostName: String
    public var workspaces: [MobileWorkspace]
    public var sessions: [MobileSessionSummary]
    public init(revision: Int, hostName: String, workspaces: [MobileWorkspace], sessions: [MobileSessionSummary]) {
        self.revision = revision; self.hostName = hostName; self.workspaces = workspaces; self.sessions = sessions
    }
}

public struct MobilePermissionField: Codable, Sendable, Equatable {
    public var label: String
    public var value: String
    public init(label: String, value: String) { self.label = label; self.value = value }
}

public struct MobilePermission: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var runId: String
    public var toolName: String
    public var title: String
    public var headline: String?
    public var fields: [MobilePermissionField]
    public var summary: String
    public var canAllow: Bool
    public var questionnaire: UserQuestionnaire?
    public init(id: String, runId: String, toolName: String, title: String, headline: String? = nil, fields: [MobilePermissionField] = [], summary: String, canAllow: Bool, questionnaire: UserQuestionnaire? = nil) {
        self.id = id; self.runId = runId; self.toolName = toolName; self.title = title; self.headline = headline; self.fields = fields
        self.summary = summary; self.canAllow = canAllow; self.questionnaire = questionnaire
    }
    /// The structured card the desktop shows, so both surfaces read alike.
    public init(request: ToolPermissionRequest) {
        let presentation = ToolPermissionPresentation.make(toolName: request.toolName, inputJSON: request.inputJSON)
        self.init(id: request.id, runId: request.runId, toolName: request.toolName, title: presentation.title, headline: presentation.headline,
                  fields: presentation.fields.map { MobilePermissionField(label: $0.label, value: $0.value) }, summary: request.summary,
                  canAllow: request.canAllow, questionnaire: request.canAnswerQuestions ? request.questionnaire : nil)
    }
}

public struct MobileUsage: Codable, Sendable, Equatable {
    public var model: String?
    public var contextUsedTokens: Int?
    public var contextWindowTokens: Int?
    public var contextPercent: Double?
    public var totalTokens: Int?
    public var costUSD: Double?
    public init(model: String? = nil, contextUsedTokens: Int? = nil, contextWindowTokens: Int? = nil, contextPercent: Double? = nil, totalTokens: Int? = nil, costUSD: Double? = nil) {
        self.model = model; self.contextUsedTokens = contextUsedTokens; self.contextWindowTokens = contextWindowTokens
        self.contextPercent = contextPercent; self.totalTokens = totalTokens; self.costUSD = costUSD
    }
}

public struct MobileQueuedItem: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var text: String
    public init(id: String, text: String) { self.id = id; self.text = text }
}

public struct MobileSessionDetail: Codable, Sendable, Equatable {
    public var `protocol`: Int = 1
    public var revision: Int
    public var session: MobileSessionSummary
    public var entries: [LogEntry]
    public var permissions: [MobilePermission]
    public var queued: [MobileQueuedItem]
    public var usage: MobileUsage?
    public var elapsedSeconds: Double?
    public static let maximumEntries = 80
    public init(revision: Int, session: MobileSessionSummary, entries: [LogEntry], permissions: [MobilePermission] = [], queued: [MobileQueuedItem] = [], usage: MobileUsage? = nil, elapsedSeconds: Double? = nil) {
        self.revision = revision; self.session = session; self.entries = entries; self.permissions = permissions; self.queued = queued; self.usage = usage; self.elapsedSeconds = elapsedSeconds
    }
}

public struct MobileSubmitRequest: Codable, Sendable { public var text: String; public init(text: String) { self.text = text } }
public struct MobileSubmitResult: Codable, Sendable, Equatable {
    public var `protocol`: Int = 1
    public var accepted: String
    public init(accepted: String) { self.accepted = accepted }
}
public struct MobileOK: Codable, Sendable, Equatable {
    public var `protocol`: Int = 1
    public var ok: Bool = true
    public init() {}
}
public struct MobileStopped: Codable, Sendable, Equatable {
    public var `protocol`: Int = 1
    public var stopped: Bool = true
    public init() {}
}
public struct MobilePermissionAnswer: Codable, Sendable {
    public var requestId: String
    public var runId: String
    public var allow: Bool
    public init(requestId: String, runId: String, allow: Bool) { self.requestId = requestId; self.runId = runId; self.allow = allow }
}
public struct MobileQuestionAnswers: Codable, Sendable {
    public var requestId: String
    public var runId: String
    public var answers: [String: UserQuestionAnswer]
    public init(requestId: String, runId: String, answers: [String: UserQuestionAnswer]) { self.requestId = requestId; self.runId = runId; self.answers = answers }
}
public struct MobileCreateSessionRequest: Codable, Sendable {
    public var kind: String
    public var provider: String?
    public init(kind: String, provider: String? = nil) { self.kind = kind; self.provider = provider }
}
public struct MobileCreatedSession: Codable, Sendable, Equatable {
    public var `protocol`: Int = 1
    public var sessionId: String
    public init(sessionId: String) { self.sessionId = sessionId }
}

/// What the desktop shows in its settings; the phone reads the QR/URL.
public struct MobileHostStatus: Codable, Sendable, Equatable {
    public var enabled: Bool
    public var relayURL: String
    public var relayConnected: Bool
    public var clients: Int
    public var serverId: String
    public var publicKeyB64: String?
    public var key: String?
    public var pairingURL: String?
    public var hostName: String
    public var detail: String
    public init(enabled: Bool = false, relayURL: String = "", relayConnected: Bool = false, clients: Int = 0, serverId: String = "", publicKeyB64: String? = nil, key: String? = nil, pairingURL: String? = nil, hostName: String = "", detail: String = "") {
        self.enabled = enabled; self.relayURL = relayURL; self.relayConnected = relayConnected; self.clients = clients; self.serverId = serverId
        self.publicKeyB64 = publicKeyB64; self.key = key; self.pairingURL = pairingURL; self.hostName = hostName; self.detail = detail
    }
}

public enum MobilePairing {
    public static let scheme = "mightyclaude"
    public static func generateKey() -> String? {
        var random = [UInt8](repeating: 0, count: 32)
        guard SecRandomCopyBytes(kSecRandomDefault, random.count, &random) == errSecSuccess else { return nil }
        return Data(random).base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }
}
