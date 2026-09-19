import Foundation

public func mightyTimestamp() -> String { ISO8601DateFormatter().string(from: Date()) }

public struct MightyError: LocalizedError, Sendable, Equatable {
    public let message: String
    public init(_ message: String) { self.message = message }
    public var errorDescription: String? { message }
}

public struct RemoteWorkspaceReference: Codable, Sendable, Equatable {
    public var connectionId: String
    public var workspaceId: String
    public var hostName: String
    public init(connectionId: String, workspaceId: String, hostName: String) {
        self.connectionId = connectionId; self.workspaceId = workspaceId; self.hostName = hostName
    }
}

public struct Workspace: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var name: String
    public var path: String
    public var createdAt: String
    public var remote: RemoteWorkspaceReference?
    public init(id: String = UUID().uuidString, name: String, path: String, createdAt: String = mightyTimestamp(), remote: RemoteWorkspaceReference? = nil) {
        self.id = id; self.name = name; self.path = path; self.createdAt = createdAt; self.remote = remote
    }
}

public struct RunSettings: Codable, Sendable, Equatable {
    public var effort: String
    public var permissionMode: String
    public var maxTurns: Int?
    public var maxBudgetUsd: Double?
    public var fastMode: Bool
    public var webSearch: String
    public var networkAccess: Bool
    public init(effort: String = "default", permissionMode: String = "manual", maxTurns: Int? = nil, maxBudgetUsd: Double? = nil, fastMode: Bool = false, webSearch: String = "default", networkAccess: Bool = false) {
        self.effort = effort; self.permissionMode = permissionMode; self.maxTurns = maxTurns; self.maxBudgetUsd = maxBudgetUsd
        self.fastMode = fastMode; self.webSearch = webSearch; self.networkAccess = networkAccess
    }
    enum CodingKeys: String, CodingKey { case effort, permissionMode, maxTurns, maxBudgetUsd, fastMode, webSearch, networkAccess }
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        effort = try c.decodeIfPresent(String.self, forKey: .effort) ?? "default"
        permissionMode = try c.decodeIfPresent(String.self, forKey: .permissionMode) ?? "manual"
        maxTurns = try c.decodeIfPresent(Int.self, forKey: .maxTurns)
        maxBudgetUsd = try c.decodeIfPresent(Double.self, forKey: .maxBudgetUsd)
        fastMode = try c.decodeIfPresent(Bool.self, forKey: .fastMode) ?? false
        webSearch = try c.decodeIfPresent(String.self, forKey: .webSearch) ?? "default"
        networkAccess = try c.decodeIfPresent(Bool.self, forKey: .networkAccess) ?? false
    }
    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(effort, forKey: .effort); try c.encode(permissionMode, forKey: .permissionMode)
        if let maxTurns { try c.encode(maxTurns, forKey: .maxTurns) } else { try c.encodeNil(forKey: .maxTurns) }
        if let maxBudgetUsd { try c.encode(maxBudgetUsd, forKey: .maxBudgetUsd) } else { try c.encodeNil(forKey: .maxBudgetUsd) }
        // Older v1 hosts reject unknown setting keys; defaults are implicit.
        if fastMode { try c.encode(fastMode, forKey: .fastMode) }
        if webSearch != "default" { try c.encode(webSearch, forKey: .webSearch) }
        if networkAccess { try c.encode(networkAccess, forKey: .networkAccess) }
    }
}
public typealias ClaudeRunSettings = RunSettings

/// Direct provider activity. Tool completion is distinct from a completed run;
/// only `kind == "turn"` terminal events describe the process lifecycle.
public struct AgentActivity: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var provider: String
    public var kind: String
    public var state: String
    public var toolName: String?
    public var summary: String
    public var output: String?
    /// Measured on the execution host; absent when no matching start was seen.
    public var durationMs: Double?
    public init(id: String = UUID().uuidString, provider: String, kind: String, state: String, toolName: String? = nil, summary: String, output: String? = nil, durationMs: Double? = nil) {
        self.id = id; self.provider = provider; self.kind = kind; self.state = state
        self.toolName = toolName; self.summary = summary; self.output = output; self.durationMs = durationMs
    }
    enum CodingKeys: String, CodingKey { case id, provider, kind, state, toolName, summary, output, durationMs }
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(String.self, forKey: .id); provider = try c.decode(String.self, forKey: .provider)
        kind = try c.decode(String.self, forKey: .kind); state = try c.decode(String.self, forKey: .state)
        toolName = try c.decodeIfPresent(String.self, forKey: .toolName); summary = try c.decode(String.self, forKey: .summary)
        output = try c.decodeIfPresent(String.self, forKey: .output)
        // A damaged optional measurement must not erase the tool's real result.
        durationMs = try? c.decodeIfPresent(Double.self, forKey: .durationMs)
        if let durationMs, !ActivitySupport.validDuration(durationMs) { self.durationMs = nil }
    }
}

/// A request typed while the pane was busy. A local Claude run receives the
/// text mid-turn; every other pane runs the item after the current request.
public struct QueuedInput: Sendable, Equatable, Identifiable {
    public var id: String
    public var text: String
    public var attachments: [RunAttachment]
    public var createdAt: String
    public static let maximumItems = 16
    public init(id: String = UUID().uuidString, text: String, attachments: [RunAttachment] = [], createdAt: String = mightyTimestamp()) {
        self.id = id; self.text = text; self.attachments = attachments; self.createdAt = createdAt
    }
    public var isEmpty: Bool { text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && attachments.isEmpty }
}

public struct LogEntry: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var kind: String
    public var text: String
    public var timestamp: String
    public var provider: String?
    public var activity: AgentActivity?
    public init(id: String = UUID().uuidString, kind: String, text: String, timestamp: String = mightyTimestamp(), provider: String? = nil, activity: AgentActivity? = nil) {
        self.id = id; self.kind = kind; self.text = text; self.timestamp = timestamp; self.provider = provider; self.activity = activity
    }
    enum CodingKeys: String, CodingKey { case id, kind, text, timestamp, provider, activity }
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(String.self, forKey: .id); kind = try c.decode(String.self, forKey: .kind)
        text = try c.decode(String.self, forKey: .text); timestamp = try c.decode(String.self, forKey: .timestamp)
        provider = try c.decodeIfPresent(String.self, forKey: .provider)
        // Damaged optional metadata must not erase the surrounding saved log.
        activity = try? c.decodeIfPresent(AgentActivity.self, forKey: .activity)
    }
}

public struct RunSession: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var workspaceId: String
    public var title: String
    public var kind: String
    public var provider: String
    public var model: String
    public var settings: RunSettings
    public var status: String
    public var logs: [LogEntry]
    public var resumeId: String?
    public var createdAt: String
    public var runTiming: AgentRunTiming?
    public var sessionUsage: SessionUsage?
    public var agentViewMode: String?
    /// How requests are made inside Mighty mode: nil is the plain CLI style,
    /// anything else the id of a registered style.
    public var mightyStyle: String?
    /// The manifest hash this pane last chose or approved. A style that comes
    /// back under the same id with different bytes does not silently rebind
    /// the pane to it (docs/mighty-styles.md §3.4).
    public var mightyStyleHash: String?
    public var graphRuns: [MightyGraphRun]?
    public var graphBlockSizes: [String: MightyGraphBlockSize]?
    public init(id: String = UUID().uuidString, workspaceId: String, title: String, kind: String = "claude", provider: String = "claude", model: String = "default", settings: RunSettings = .init(), status: String = "idle", logs: [LogEntry] = [], resumeId: String? = nil, createdAt: String = mightyTimestamp(), runTiming: AgentRunTiming? = nil, sessionUsage: SessionUsage? = nil) {
        self.id = id; self.workspaceId = workspaceId; self.title = title; self.kind = kind; self.provider = provider; self.model = model; self.settings = settings; self.status = status; self.logs = logs; self.resumeId = resumeId; self.createdAt = createdAt; self.runTiming = runTiming; self.sessionUsage = sessionUsage
    }
    enum CodingKeys: String, CodingKey { case id, workspaceId, title, kind, provider, model, settings, status, logs, resumeId, createdAt, runTiming, sessionUsage, agentViewMode, mightyStyle, mightyStyleHash, graphRuns, graphBlockSizes }
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(String.self, forKey: .id); workspaceId = try c.decode(String.self, forKey: .workspaceId)
        title = try c.decodeIfPresent(String.self, forKey: .title) ?? "Claude"; kind = try c.decodeIfPresent(String.self, forKey: .kind) ?? "claude"
        provider = try c.decodeIfPresent(String.self, forKey: .provider) ?? "claude"; model = try c.decodeIfPresent(String.self, forKey: .model) ?? "default"
        settings = try c.decodeIfPresent(RunSettings.self, forKey: .settings) ?? .init(); status = try c.decodeIfPresent(String.self, forKey: .status) ?? "idle"
        logs = try c.decodeIfPresent([LogEntry].self, forKey: .logs) ?? []; resumeId = try c.decodeIfPresent(String.self, forKey: .resumeId)
        createdAt = try c.decodeIfPresent(String.self, forKey: .createdAt) ?? mightyTimestamp()
        // Optional timing damage must not discard the saved conversation.
        runTiming = try? c.decodeIfPresent(AgentRunTiming.self, forKey: .runTiming)
        sessionUsage = try? c.decodeIfPresent(SessionUsage.self, forKey: .sessionUsage)
        agentViewMode = try? c.decodeIfPresent(String.self, forKey: .agentViewMode)
        mightyStyle = try? c.decodeIfPresent(String.self, forKey: .mightyStyle)
        mightyStyleHash = try? c.decodeIfPresent(String.self, forKey: .mightyStyleHash)
        graphRuns = try? c.decodeIfPresent([MightyGraphRun].self, forKey: .graphRuns)
        // Optional layout damage must not discard the saved conversation.
        graphBlockSizes = try? c.decodeIfPresent([String: MightyGraphBlockSize].self, forKey: .graphBlockSizes)
    }
}

public struct AppSnapshot: Codable, Sendable, Equatable {
    public var version: Int
    public var workspaces: [Workspace]
    public var sessions: [RunSession]
    public var activeWorkspaceId: String?
    public var activeSessionId: String?
    public var layout: String
    public var theme: String
    public var sidebarWidth: Double
    public var paneLayouts: [String: PaneLayoutNode]?
    public var paneLayoutModes: [String: String]?
    public var paneLayoutActiveSessionIds: [String: String]?
    public var autoUpdateCLIs: Bool?
    /// Sidebar workspaces whose pane list is open. nil (older state) means only
    /// the active workspace is open, which was the previous behaviour.
    public var expandedWorkspaceIds: [String]?
    /// Phone access over Tailscale; nil means never enabled.
    public var mobileRemote: MobileRemoteSettings?
    public init(version: Int = 1, workspaces: [Workspace] = [], sessions: [RunSession] = [], activeWorkspaceId: String? = nil, activeSessionId: String? = nil, layout: String = "grid", theme: String = "dark", sidebarWidth: Double = 252, paneLayouts: [String: PaneLayoutNode]? = nil, paneLayoutModes: [String: String]? = nil, paneLayoutActiveSessionIds: [String: String]? = nil, autoUpdateCLIs: Bool? = nil, expandedWorkspaceIds: [String]? = nil, mobileRemote: MobileRemoteSettings? = nil) {
        self.version = version; self.workspaces = workspaces; self.sessions = sessions; self.activeWorkspaceId = activeWorkspaceId; self.activeSessionId = activeSessionId; self.layout = layout; self.theme = theme; self.sidebarWidth = sidebarWidth
        self.paneLayouts = paneLayouts
        self.paneLayoutModes = paneLayoutModes; self.paneLayoutActiveSessionIds = paneLayoutActiveSessionIds
        self.autoUpdateCLIs = autoUpdateCLIs; self.expandedWorkspaceIds = expandedWorkspaceIds; self.mobileRemote = mobileRemote
    }
}

public struct StartRunRequest: Codable, Sendable, Equatable {
    public var sessionId: String
    public var workspaceId: String
    public var kind: String
    public var input: String
    public var model: String
    public var provider: String
    public var settings: RunSettings
    public var resumeId: String?
    public var attachments: [RunAttachment]
    public init(sessionId: String, workspaceId: String, kind: String = "claude", input: String, model: String = "default", provider: String = "claude", settings: RunSettings = .init(), resumeId: String? = nil, attachments: [RunAttachment] = []) {
        self.sessionId = sessionId; self.workspaceId = workspaceId; self.kind = kind; self.input = input; self.model = model; self.provider = provider; self.settings = settings; self.resumeId = resumeId; self.attachments = attachments
    }
    enum CodingKeys: String, CodingKey { case sessionId, workspaceId, kind, input, model, provider, settings, resumeId, attachments }
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        sessionId = try c.decode(String.self, forKey: .sessionId); workspaceId = try c.decode(String.self, forKey: .workspaceId)
        kind = try c.decode(String.self, forKey: .kind); input = try c.decode(String.self, forKey: .input); model = try c.decode(String.self, forKey: .model)
        provider = try c.decodeIfPresent(String.self, forKey: .provider) ?? "claude"; settings = try c.decodeIfPresent(RunSettings.self, forKey: .settings) ?? .init()
        resumeId = try c.decodeIfPresent(String.self, forKey: .resumeId)
        attachments = try c.decodeIfPresent([RunAttachment].self, forKey: .attachments) ?? []
    }
    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(sessionId, forKey: .sessionId); try c.encode(workspaceId, forKey: .workspaceId)
        try c.encode(kind, forKey: .kind); try c.encode(input, forKey: .input); try c.encode(model, forKey: .model)
        try c.encode(provider, forKey: .provider); try c.encode(settings, forKey: .settings); try c.encodeIfPresent(resumeId, forKey: .resumeId)
        if !attachments.isEmpty { try c.encode(attachments, forKey: .attachments) }
    }
}

public struct RunEvent: Codable, Sendable, Equatable {
    public var sessionId: String
    public var type: String
    public var entry: LogEntry?
    public var status: String?
    public var resumeId: String?
    public var activity: AgentActivity?
    public var permission: ToolPermissionRequest?
    public var usage: SessionUsage?
    public var graph: ExecutionGraphNode?
    public init(sessionId: String, type: String, entry: LogEntry? = nil, status: String? = nil, resumeId: String? = nil, activity: AgentActivity? = nil, permission: ToolPermissionRequest? = nil, usage: SessionUsage? = nil, graph: ExecutionGraphNode? = nil) {
        self.sessionId = sessionId; self.type = type; self.entry = entry; self.status = status; self.resumeId = resumeId; self.activity = activity; self.permission = permission; self.usage = usage
        self.graph = graph
    }
    enum CodingKeys: String, CodingKey { case sessionId, type, entry, status, resumeId, activity, permission, usage, graph }
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        sessionId = try c.decode(String.self, forKey: .sessionId); type = try c.decode(String.self, forKey: .type)
        entry = try c.decodeIfPresent(LogEntry.self, forKey: .entry); status = try c.decodeIfPresent(String.self, forKey: .status)
        resumeId = try c.decodeIfPresent(String.self, forKey: .resumeId); activity = try c.decodeIfPresent(AgentActivity.self, forKey: .activity)
        permission = try c.decodeIfPresent(ToolPermissionRequest.self, forKey: .permission)
        usage = try? c.decodeIfPresent(SessionUsage.self, forKey: .usage)
        graph = try? c.decodeIfPresent(ExecutionGraphNode.self, forKey: .graph)
    }
}

public struct ModelOption: Codable, Sendable, Equatable, Identifiable {
    public var value: String
    public var displayName: String
    public var description: String
    public var resolvedModel: String?
    public var supportsEffort: Bool?
    public var supportedEffortLevels: [String]?
    public var id: String { value }
    public init(value: String, displayName: String, description: String = "", resolvedModel: String? = nil, supportsEffort: Bool? = nil, supportedEffortLevels: [String]? = nil) {
        self.value = value; self.displayName = displayName; self.description = description; self.resolvedModel = resolvedModel; self.supportsEffort = supportsEffort; self.supportedEffortLevels = supportedEffortLevels
    }
}
public struct ModelCatalog: Codable, Sendable, Equatable {
    public var source: String
    public var models: [ModelOption]
    public var detail: String
    public init(source: String = "fallback", models: [ModelOption] = [], detail: String = "") { self.source = source; self.models = models; self.detail = detail }
}
public typealias ClaudeModelOption = ModelOption
public typealias ClaudeModelCatalog = ModelCatalog

public struct ProviderCapabilities: Codable, Sendable, Equatable {
    public var effort: Bool
    public var permissionModes: [String]
    public var maxTurns: Bool
    public var maxBudgetUsd: Bool
    public var resume: Bool
    public var fastMode: Bool
    public var webSearch: Bool
    public var networkAccess: Bool
    public var attachments: Bool
    public init(effort: Bool = true, permissionModes: [String] = ["manual", "plan", "acceptEdits"], maxTurns: Bool = true, maxBudgetUsd: Bool = true, resume: Bool = true, fastMode: Bool = false, webSearch: Bool = false, networkAccess: Bool = false, attachments: Bool = false) { self.attachments = attachments; self.effort = effort; self.permissionModes = permissionModes; self.maxTurns = maxTurns; self.maxBudgetUsd = maxBudgetUsd; self.resume = resume; self.fastMode = fastMode; self.webSearch = webSearch; self.networkAccess = networkAccess }
    enum CodingKeys: String, CodingKey { case effort, permissionModes, maxTurns, maxBudgetUsd, resume, fastMode, webSearch, networkAccess, attachments }
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        effort = try c.decode(Bool.self, forKey: .effort); permissionModes = try c.decode([String].self, forKey: .permissionModes)
        maxTurns = try c.decode(Bool.self, forKey: .maxTurns); maxBudgetUsd = try c.decode(Bool.self, forKey: .maxBudgetUsd); resume = try c.decode(Bool.self, forKey: .resume)
        fastMode = try c.decodeIfPresent(Bool.self, forKey: .fastMode) ?? false
        webSearch = try c.decodeIfPresent(Bool.self, forKey: .webSearch) ?? false
        networkAccess = try c.decodeIfPresent(Bool.self, forKey: .networkAccess) ?? false
        attachments = try c.decodeIfPresent(Bool.self, forKey: .attachments) ?? false
    }
}
public struct ProviderRuntime: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var name: String
    public var available: Bool
    public var version: String?
    public var detail: String
    public var modelCatalog: ModelCatalog
    public var capabilities: ProviderCapabilities
    public init(id: String, name: String, available: Bool = false, version: String? = nil, detail: String = "", modelCatalog: ModelCatalog = .init(), capabilities: ProviderCapabilities = .init()) { self.id = id; self.name = name; self.available = available; self.version = version; self.detail = detail; self.modelCatalog = modelCatalog; self.capabilities = capabilities }
}
public struct ModsRuntime: Codable, Sendable, Equatable {
    public var status: String
    public var minimumVersion: String
    public var detail: String
    public init(status: String = "unavailable", minimumVersion: String = "2.1.271", detail: String = "") { self.status = status; self.minimumVersion = minimumVersion; self.detail = detail }
}
public struct RuntimeInfo: Codable, Sendable, Equatable {
    public var platform: String
    public var appVersion: String
    public var claudeAvailable: Bool
    public var claudeVersion: String?
    public var modelCatalog: ModelCatalog?
    public var providers: [ProviderRuntime]?
    public var mods: ModsRuntime?
    public init(platform: String = "darwin", appVersion: String = "0.1.0", claudeAvailable: Bool = false, claudeVersion: String? = nil, modelCatalog: ModelCatalog? = nil, providers: [ProviderRuntime]? = nil, mods: ModsRuntime? = nil) { self.platform = platform; self.appVersion = appVersion; self.claudeAvailable = claudeAvailable; self.claudeVersion = claudeVersion; self.modelCatalog = modelCatalog; self.providers = providers; self.mods = mods }
}

public enum ProviderOptions {
    public static let ids = ["claude", "codex", "gemini"]
    public static let efforts = ["low", "medium", "high", "xhigh", "max"]
    public static let webSearchModes = ["default", "disabled", "cached", "live"]
    /// Schema support preserves saved choices independently of CLI discovery.
    /// Runtime advertisements deliberately omit Auto until a supported CLI is found.
    public static func permissionModes(provider: String, includeAuto: Bool = true) -> [String] {
        if provider == "codex" { return ["manual", "acceptEdits", "fullAccess"] }
        if provider == "claude", includeAuto { return ["plan", "manual", "acceptEdits", "auto", "fullAccess"] }
        return ["manual", "plan", "acceptEdits", "fullAccess"]
    }
    public static func normalizeProvider(_ value: String) -> String { ids.contains(value) ? value : "claude" }
    public static func label(_ id: String) -> String { ["claude": "Claude", "codex": "Codex", "gemini": "Gemini"][id] ?? "Claude" }
    public static func fallbackCatalog(_ id: String) -> ModelCatalog {
        let names = id == "codex" ? ["default", "gpt-5.6-sol", "gpt-6-astra"] : id == "gemini" ? ["default", "auto", "gemini-3-pro-preview", "gemini-3-flash-preview", "gemini-2.5-pro", "gemini-2.5-flash"] : ["default", "best", "fable", "opus", "sonnet", "haiku", "opusplan"]
        return ModelCatalog(models: names.map { ModelOption(value: $0, displayName: $0 == "default" ? "\(label(id)) 설정 따름" : $0, description: $0 == "default" ? "CLI 설정 또는 재개한 세션의 모델을 사용합니다." : "공식 모델 이름 예시 · 사용 가능 여부는 CLI 계정과 제공자 설정에 따릅니다.", supportsEffort: $0 == "haiku" ? false : nil) }, detail: "공식 모델 이름 예시입니다. 사용 가능 여부에는 CLI 계정·제공자 설정이 적용됩니다.")
    }
    public static func fallbackRuntime(_ id: String) -> ProviderRuntime {
        ProviderRuntime(id: id, name: id == "claude" ? "Claude Code" : "\(label(id)) CLI", detail: "CLI 설치 상태를 확인해 주세요.", modelCatalog: fallbackCatalog(id), capabilities: ProviderCapabilities(effort: id != "gemini", permissionModes: permissionModes(provider: id, includeAuto: false), maxTurns: id == "claude", maxBudgetUsd: id == "claude", fastMode: id == "codex", webSearch: id == "codex", networkAccess: id == "codex", attachments: true))
    }
    public static func effortLevels(provider: String, model: String, catalog: ModelCatalog? = nil) -> [String] {
        if provider == "gemini" { return [] }
        let option = catalog?.models.first { $0.value == model || $0.resolvedModel == model }
        if option?.supportsEffort == false { return [] }
        if let levels = option?.supportedEffortLevels { return levels.filter { efforts.contains($0) } }
        if provider == "codex" || model.lowercased().contains("haiku") { return [] }
        if model.range(of: "(?:opus|sonnet)[-.]4[-.]6", options: .regularExpression) != nil { return efforts.filter { $0 != "xhigh" } }
        if model.range(of: "^(?:default|best|fable|opus|sonnet|opusplan)(?:\\[1m\\])?$|(?:fable[-.]5|opus[-.](?:5|4[-.][78])|sonnet[-.]5)", options: .regularExpression) != nil { return efforts }
        return option?.supportsEffort == true ? ["low", "medium", "high"] : []
    }
    public static func normalizedSettings(provider: String, settings: RunSettings) -> RunSettings {
        var caps = fallbackRuntime(provider).capabilities
        caps.permissionModes = permissionModes(provider: provider)
        return RunSettings(effort: caps.effort && efforts.contains(settings.effort) ? settings.effort : "default", permissionMode: caps.permissionModes.contains(settings.permissionMode) ? settings.permissionMode : "manual", maxTurns: caps.maxTurns && (1...1000).contains(settings.maxTurns ?? 0) ? settings.maxTurns : nil, maxBudgetUsd: caps.maxBudgetUsd && (settings.maxBudgetUsd ?? 0).isFinite && (settings.maxBudgetUsd ?? 0) > 0 && (settings.maxBudgetUsd ?? 0) <= 10_000 ? settings.maxBudgetUsd : nil, fastMode: caps.fastMode && settings.fastMode, webSearch: caps.webSearch && webSearchModes.contains(settings.webSearch) ? settings.webSearch : "default", networkAccess: caps.networkAccess && settings.permissionMode == "acceptEdits" && settings.networkAccess)
    }
}

public enum CoreValidation {
    public static func identifier(_ value: String) -> Bool { value.range(of: "^[a-zA-Z0-9][a-zA-Z0-9._:-]{0,127}$", options: .regularExpression) != nil }
    public static func model(_ value: String) -> Bool { value.count <= 200 && value.range(of: "^[a-zA-Z0-9][a-zA-Z0-9._:/@\\[\\]-]*$", options: .regularExpression) != nil }
    public static func validate(_ request: StartRunRequest) throws {
        guard identifier(request.sessionId), identifier(request.workspaceId), ["claude", "shell"].contains(request.kind), ProviderOptions.ids.contains(request.provider), model(request.model) else { throw MightyError("실행 요청 형식이 올바르지 않습니다.") }
        try AttachmentSupport.validate(request.attachments)
        if request.kind == "shell", !request.attachments.isEmpty { throw MightyError("명령 창에는 첨부 파일을 보낼 수 없습니다.") }
        guard !request.input.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || !request.attachments.isEmpty, request.input.utf8.count <= 400_000, request.input.count <= 100_000, !request.input.contains("\0") else { throw MightyError("실행 입력은 비어 있지 않은 100,000자 이하의 텍스트여야 합니다.") }
        if let resume = request.resumeId, !identifier(resume) { throw MightyError("CLI 세션 ID가 올바르지 않습니다.") }
        let s = request.settings
        guard (["default"] + ProviderOptions.efforts).contains(s.effort), ["manual", "plan", "acceptEdits", "auto", "fullAccess"].contains(s.permissionMode), ProviderOptions.webSearchModes.contains(s.webSearch), s.maxTurns == nil || (1...1000).contains(s.maxTurns!), s.maxBudgetUsd == nil || (s.maxBudgetUsd!.isFinite && s.maxBudgetUsd! > 0 && s.maxBudgetUsd! <= 10_000) else { throw MightyError("실행 설정이 올바르지 않습니다.") }
        if s.permissionMode == "auto", request.kind != "claude" || request.provider != "claude" { throw MightyError("Auto mode는 Claude 실행 창에서만 사용할 수 있습니다.") }
        if request.kind == "shell", s.fastMode || s.webSearch != "default" || s.networkAccess { throw MightyError("명령 창은 AI 실행 설정을 지원하지 않습니다.") }
        if request.kind == "claude" {
            var caps = ProviderOptions.fallbackRuntime(request.provider).capabilities
            caps.permissionModes = ProviderOptions.permissionModes(provider: request.provider)
            try validateCapabilities(request, capabilities: caps)
            if request.provider == "claude", request.model.lowercased().contains("haiku"), s.effort != "default" { throw MightyError("Haiku는 추론 강도 설정을 지원하지 않습니다.") }
        }
    }
    public static func validateCapabilities(_ request: StartRunRequest, capabilities caps: ProviderCapabilities) throws {
        let s = request.settings
        guard caps.attachments || request.attachments.isEmpty else { throw MightyError("이 실행기가 첨부 파일을 지원하지 않습니다. 원격 앱을 업데이트하세요.") }
        guard caps.permissionModes.contains(s.permissionMode), caps.effort || s.effort == "default", caps.maxTurns || s.maxTurns == nil, caps.maxBudgetUsd || s.maxBudgetUsd == nil, caps.fastMode || !s.fastMode, caps.webSearch || s.webSearch == "default", caps.networkAccess || !s.networkAccess, !s.networkAccess || (request.provider == "codex" && s.permissionMode == "acceptEdits"), caps.resume || request.resumeId == nil else { throw MightyError("선택한 실행기가 이 실행 설정을 지원하지 않습니다. 실행기 또는 원격 앱을 확인해 주세요.") }
    }
    public static func validateSelection(_ request: StartRunRequest, catalog: ModelCatalog) throws {
        try validate(request)
        if request.provider == "claude" {
            let official = ProviderOptions.fallbackCatalog("claude").models.contains { $0.value == request.model } || request.model.range(of: "^(?:(?:opus|sonnet|fable)\\[1m\\]|claude-(?:(?:opus|sonnet|haiku|fable)-[0-9]+(?:[-.][0-9]+)*|[0-9]+(?:-[0-9]+)*-(?:opus|sonnet|haiku|fable)(?:-[0-9]+)*)(?:\\[1m\\])?)$", options: .regularExpression) != nil
            guard official || catalog.models.contains(where: { $0.value == request.model || $0.resolvedModel == request.model }) else { throw MightyError("Claude 모델 목록에서 선택한 모델을 확인하지 못했습니다.") }
        }
        if request.settings.effort != "default", !ProviderOptions.effortLevels(provider: request.provider, model: request.model, catalog: catalog).contains(request.settings.effort) { throw MightyError("선택한 모델의 추론 강도를 확인할 수 없습니다. CLI 기본값을 선택해 주세요.") }
    }
}
