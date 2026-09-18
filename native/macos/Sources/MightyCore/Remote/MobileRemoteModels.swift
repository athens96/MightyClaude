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
    public var capabilities: [String]
    public init(hostId: String, hostName: String, appVersion: String, platform: String = "darwin", capabilities: [String] = MobileCapability.all) {
        self.hostId = hostId; self.hostName = hostName; self.appVersion = appVersion; self.platform = platform; self.capabilities = capabilities
    }
}

/// The m1 extensions this host implements. A phone shows a feature only when
/// its name is listed, so a name appears here once the route behind it works.
public enum MobileCapability {
    public static let all = ["submit-mode", "queue", "pane", "history", "settings", "commands", "status"]
}

/// The fixed string vocabularies of the extension (docs/mobile-remote.md,
/// "고정 문자열 값"). A value outside them is refused, never defaulted.
public enum MobileWire {
    public static let submitModes = ["steer", "queue"]
    public static let agentViewModes = ["plain", "mighty"]
    public static let mightyStyles = ["cli", "ouroboros", "paperthin"]
    /// What `POST …/command` performs; the other actions the phone handles itself.
    public static let performedActions = ["clear", "usage", "help"]
    /// The Mac's `agentViewMode` is nil or "default" where the wire says "plain".
    public static let plainViewMode = "plain"
    /// The Mac's `mightyStyle` is nil where the wire says "cli".
    public static let cliStyle = "cli"
    public static let maximumTitle = 80
    public static let maximumPageLimit = 100
    public static let defaultPageLimit = 50
}

/// What submitting text finally did. The app's own submit path answers in
/// these terms so the bridge never has to guess: steering can still end up
/// queued, and a queued item the host started right away is `started`.
/// `dropped` has no wire name — nothing was accepted, so the phone gets 409.
public enum SubmitOutcome: String, Sendable, Equatable, CaseIterable {
    case started, steered, queued, dropped
    /// The `accepted` value for this outcome, or nil when it must be refused.
    public var accepted: String? { self == .dropped ? nil : rawValue }
}

/// What a host method refuses with when the contract asks for a status other
/// than 409; a plain `MightyError` keeps its existing meaning ("cannot now").
public enum MobileHostError: Error, Sendable, Equatable {
    case badRequest(String)
    case notFound(String)
    case conflict(String)
    public var status: Int {
        switch self {
        case .badRequest: return 400
        case .notFound: return 404
        case .conflict: return 409
        }
    }
    public var message: String {
        switch self {
        case .badRequest(let text), .notFound(let text), .conflict(let text): return text
        }
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
    public var agentViewMode: String?
    public var mightyStyle: String?
    public init(id: String, workspaceId: String, title: String, kind: String, provider: String, model: String, status: String, revision: Int, updatedAt: String,
                preview: MobilePreview? = nil, pendingPermissions: Int = 0, pendingQuestions: Int = 0, queued: Int = 0, resumeId: String? = nil, terminal: Bool = false,
                agentViewMode: String? = nil, mightyStyle: String? = nil) {
        self.id = id; self.workspaceId = workspaceId; self.title = title; self.kind = kind; self.provider = provider; self.model = model; self.status = status
        self.revision = revision; self.updatedAt = updatedAt; self.preview = preview; self.pendingPermissions = pendingPermissions
        self.pendingQuestions = pendingQuestions; self.queued = queued; self.resumeId = resumeId; self.terminal = terminal
        self.agentViewMode = agentViewMode; self.mightyStyle = mightyStyle
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
    /// True when the host still holds entries older than the ones sent here.
    public var hasOlder: Bool?
    public var settings: MobileSettings?
    public var statusLine: MobileStatusLine?
    public var rateLimits: [MobileRateLimit]?
    public static let maximumEntries = 80
    public init(revision: Int, session: MobileSessionSummary, entries: [LogEntry], permissions: [MobilePermission] = [], queued: [MobileQueuedItem] = [], usage: MobileUsage? = nil, elapsedSeconds: Double? = nil,
                hasOlder: Bool? = nil, settings: MobileSettings? = nil, statusLine: MobileStatusLine? = nil, rateLimits: [MobileRateLimit]? = nil) {
        self.revision = revision; self.session = session; self.entries = entries; self.permissions = permissions; self.queued = queued; self.usage = usage; self.elapsedSeconds = elapsedSeconds
        self.hasOlder = hasOlder; self.settings = settings; self.statusLine = statusLine; self.rateLimits = rateLimits
    }
}

/// One choice of a settings picker, in the same order the Mac's menu shows.
public struct MobileOption: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var label: String
    public init(id: String, label: String) { self.id = id; self.label = label }
}

public struct MobileSettingsOptions: Codable, Sendable, Equatable {
    public var models: [MobileOption]
    public var permissionModes: [MobileOption]
    /// Absent when the provider has no reasoning-effort capability at all, so
    /// the phone shows no picker rather than one with a single dead choice.
    public var efforts: [MobileOption]?
    public var mightyStyles: [MobileOption]
    public init(models: [MobileOption] = [], permissionModes: [MobileOption] = [], efforts: [MobileOption]? = nil, mightyStyles: [MobileOption] = []) {
        self.models = models; self.permissionModes = permissionModes; self.efforts = efforts; self.mightyStyles = mightyStyles
    }
}

public struct MobileSettings: Codable, Sendable, Equatable {
    /// False while the pane is running or a run is pending, matching the Mac's
    /// disabled composer pickers.
    public var editable: Bool
    public var model: String
    public var permissionMode: String
    /// Absent together with `options.efforts` when the provider has no
    /// reasoning-effort capability.
    public var effort: String?
    public var agentViewMode: String
    public var mightyStyle: String
    public var options: MobileSettingsOptions
    public init(editable: Bool, model: String, permissionMode: String, effort: String? = nil, agentViewMode: String, mightyStyle: String, options: MobileSettingsOptions) {
        self.editable = editable; self.model = model; self.permissionMode = permissionMode; self.effort = effort
        self.agentViewMode = agentViewMode; self.mightyStyle = mightyStyle; self.options = options
    }
}

/// One drawn run of the pane's status line, with the SGR state flattened to
/// what a phone can paint.
public struct MobileStatusSegment: Codable, Sendable, Equatable {
    public var text: String
    public var fg: String?
    public var bold: Bool?
    public init(text: String, fg: String? = nil, bold: Bool? = nil) { self.text = text; self.fg = fg; self.bold = bold }
}

public struct MobileStatusLine: Codable, Sendable, Equatable {
    public var lines: [[MobileStatusSegment]]
    public init(lines: [[MobileStatusSegment]]) { self.lines = lines }
}

public struct MobileRateLimit: Codable, Sendable, Equatable {
    public var label: String
    public var usedPercent: Double
    public var resetsAt: String?
    public init(label: String, usedPercent: Double, resetsAt: String? = nil) { self.label = label; self.usedPercent = usedPercent; self.resetsAt = resetsAt }
}

/// A slash command the phone may list. `action` marks the ones it performs
/// itself; without one it inserts `/name ` into the composer.
public struct MobileCommand: Codable, Sendable, Equatable {
    public var name: String
    public var description: String
    public var source: String
    public var argumentHint: String?
    public var action: String?
    public init(name: String, description: String, source: String, argumentHint: String? = nil, action: String? = nil) {
        self.name = name; self.description = description; self.source = source; self.argumentHint = argumentHint; self.action = action
    }
}

public struct MobileCommandList: Codable, Sendable, Equatable {
    public var `protocol`: Int = 1
    public var commands: [MobileCommand]
    public init(commands: [MobileCommand]) { self.commands = commands }
}

public struct MobileEntriesPage: Codable, Sendable, Equatable {
    public var `protocol`: Int = 1
    public var entries: [LogEntry]
    public var hasMore: Bool
    public init(entries: [LogEntry], hasMore: Bool) { self.entries = entries; self.hasMore = hasMore }
}

public struct MobileCommandResult: Codable, Sendable, Equatable {
    public var `protocol`: Int = 1
    public var ok: Bool = true
    public var message: String?
    public init(message: String? = nil) { self.message = message }
}

public struct MobileSubmitRequest: Codable, Sendable {
    public var text: String
    public var mode: String?
    public init(text: String, mode: String? = nil) { self.text = text; self.mode = mode }
}
public struct MobileRenameRequest: Codable, Sendable { public var title: String; public init(title: String) { self.title = title } }
public struct MobileCommandRequest: Codable, Sendable { public var action: String; public init(action: String) { self.action = action } }
public struct MobileSettingsRequest: Codable, Sendable, Equatable {
    public var model: String?
    public var permissionMode: String?
    public var effort: String?
    public var agentViewMode: String?
    public var mightyStyle: String?
    public init(model: String? = nil, permissionMode: String? = nil, effort: String? = nil, agentViewMode: String? = nil, mightyStyle: String? = nil) {
        self.model = model; self.permissionMode = permissionMode; self.effort = effort; self.agentViewMode = agentViewMode; self.mightyStyle = mightyStyle
    }
}
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
