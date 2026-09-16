import Foundation

typealias RemoteFailure = MightyError

public struct TailscaleState: Codable, Sendable, Equatable {
    public var available: Bool
    public var addresses: [String]
    public var deviceName: String?
    public var detail: String
    public init(available: Bool = false, addresses: [String] = [], deviceName: String? = nil, detail: String = "Tailscale 설치·연결 상태를 확인합니다.") {
        self.available = available; self.addresses = addresses; self.deviceName = deviceName; self.detail = detail
    }
}
public struct RemoteHostState: Codable, Sendable, Equatable {
    public var enabled: Bool
    public var address: String?
    public var token: String?
    public var port: Int?
    public var workspaceIds: [String]
    public var activeRuns: Int
    public var detail: String?
    public init(enabled: Bool = false, address: String? = nil, token: String? = nil, port: Int? = nil, workspaceIds: [String] = [], activeRuns: Int = 0, detail: String? = "공유가 꺼져 있습니다. 앱을 다시 시작하면 항상 꺼집니다.") {
        self.enabled = enabled; self.address = address; self.token = token; self.port = port; self.workspaceIds = workspaceIds; self.activeRuns = activeRuns; self.detail = detail
    }
}
public struct RemoteConnectionInfo: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var name: String
    public var address: String
    public var status: String
    public var hostId: String?
    public var hostName: String?
    public var workspaces: [Workspace]
    public var runtime: RuntimeInfo?
    public var detail: String?
    public init(id: String = UUID().uuidString, name: String, address: String, status: String = "disconnected", hostId: String? = nil, hostName: String? = nil, workspaces: [Workspace] = [], runtime: RuntimeInfo? = nil, detail: String? = nil) {
        self.id = id; self.name = name; self.address = address; self.status = status; self.hostId = hostId; self.hostName = hostName; self.workspaces = workspaces; self.runtime = runtime; self.detail = detail
    }
}
public struct RemoteState: Codable, Sendable, Equatable {
    public var tailscale: TailscaleState
    public var host: RemoteHostState
    public var connections: [RemoteConnectionInfo]
    public init(tailscale: TailscaleState = .init(), host: RemoteHostState = .init(), connections: [RemoteConnectionInfo] = []) {
        self.tailscale = tailscale; self.host = host; self.connections = connections
    }
}
public struct ShareRequest: Codable, Sendable {
    public var workspaceIds: [String]
    public var port: Int?
    public init(workspaceIds: [String], port: Int? = nil) { self.workspaceIds = workspaceIds; self.port = port }
}
public struct ConnectRemoteRequest: Codable, Sendable {
    public var name: String
    public var address: String
    public var token: String
    public init(name: String, address: String, token: String) { self.name = name; self.address = address; self.token = token }
}
struct WireInfo: Codable, Sendable {
    var `protocol`: Int = 1
    var hostId: String
    var hostName: String
    var workspaces: [Workspace]
    var runtime: RuntimeInfo
}
struct WireEvent: Codable, Sendable { var cursor: Int; var event: RunEvent }
struct WirePoll: Codable, Sendable {
    var `protocol`: Int = 1
    var cursor: Int
    var lastCursor: Int
    var gap: Bool
    var done: Bool
    var events: [WireEvent]
}
struct WireStart: Codable, Sendable { var request: StartRunRequest }
struct WireJob: Codable, Sendable { var `protocol`: Int; var jobId: String }

enum RemoteValidation {
    static func token(_ value: String) -> Bool { value.range(of: "^[a-zA-Z0-9_-]{43,128}$", options: .regularExpression) != nil }
    static func workspace(_ value: Workspace) -> Bool {
        CoreValidation.identifier(value.id) && value.remote == nil && value.path.count <= 4096 && !value.path.contains("\0") &&
        (value.path.hasPrefix("/") || value.path.hasPrefix("\\\\") || value.path.range(of: "^[a-zA-Z]:[\\\\/]", options: .regularExpression) != nil)
    }
    static func event(_ value: RunEvent, sessionId: String) -> Bool {
        guard value.sessionId == sessionId else { return false }
        if let activity = value.activity, !ActivitySupport.valid(activity) { return false }
        switch value.type {
        // Damaged optional usage must not erase otherwise valid remote output.
        // The client normalizes (or ignores) this observation before publishing.
        case "usage": return true
        case "graph": return true // Optional graph observations are normalized independently.
        case "activity": return value.activity != nil
        case "status": return ["idle", "running", "completed", "error", "stopped"].contains(value.status ?? "")
        case "resume": return CoreValidation.identifier(value.resumeId ?? "")
        case "log":
            guard let entry = value.entry else { return false }
            if let activity = entry.activity, !ActivitySupport.valid(activity) { return false }
            return CoreValidation.identifier(entry.id) && ["user", "assistant", "system", "output", "error"].contains(entry.kind) && entry.text.utf8.count <= 131_072 && (entry.provider == nil || ProviderOptions.ids.contains(entry.provider!))
        default: return false
        }
    }
    static func info(_ value: WireInfo) throws {
        guard value.protocol == 1, CoreValidation.identifier(value.hostId), value.hostName.count <= 512,
              value.workspaces.count <= 64, value.workspaces.allSatisfy(workspace), Set(value.workspaces.map(\.id)).count == value.workspaces.count,
              ["darwin", "win32", "linux", "browser"].contains(value.runtime.platform), value.runtime.appVersion.count <= 80 else {
            throw RemoteFailure("호환되는 MightyClaude 호스트 응답이 아닙니다.")
        }
        for provider in value.runtime.providers ?? [] {
            guard ProviderOptions.ids.contains(provider.id), provider.modelCatalog.models.count <= 128,
                  ["cli", "fallback", "preview"].contains(provider.modelCatalog.source),
                  provider.modelCatalog.models.allSatisfy({ CoreValidation.model($0.value) && $0.displayName.count <= 160 && $0.description.count <= 2400 }),
                  provider.capabilities.permissionModes.allSatisfy({ ["manual", "plan", "acceptEdits", "fullAccess"].contains($0) || $0 == "auto" && provider.id == "claude" }) else { throw RemoteFailure("원격 실행 환경 형식이 올바르지 않습니다.") }
        }
    }
}
