import Foundation

/// Account quota windows; these are independent of an agent's context window.
public struct AccountUsageWindow: Codable, Equatable, Sendable, Identifiable {
    public var kind: String
    public var usedPercent: Double
    public var resetsAt: String?
    public var windowMinutes: Int?
    public var id: String { kind }
    public init(kind: String, usedPercent: Double, resetsAt: String? = nil, windowMinutes: Int? = nil) {
        self.kind = kind; self.usedPercent = usedPercent; self.resetsAt = resetsAt; self.windowMinutes = windowMinutes
    }
}

/// Memory-only presentation data. Never contains credentials, raw HTTP bodies or transcripts.
public struct AccountUsageSnapshot: Codable, Equatable, Sendable {
    public var provider: String
    public var accountLabel: String?
    public var plan: String?
    public var windows: [AccountUsageWindow]
    public var fetchedAt: String?
    /// available, unavailable, error, stale, cancelled, or permission (the
    /// login keychain has not yet allowed this app to read the credential).
    public var status: String
    public var detail: String
    /// Transport scheduling hint; excluded from presentation serialization.
    var retryAfterSeconds: TimeInterval?
    enum CodingKeys: String, CodingKey { case provider, accountLabel, plan, windows, fetchedAt, status, detail }
    public init(provider: String, accountLabel: String? = nil, plan: String? = nil,
                windows: [AccountUsageWindow] = [], fetchedAt: String? = nil,
                status: String = "unavailable", detail: String = "계정 사용량을 아직 확인하지 않았습니다.") {
        self.provider = provider; self.accountLabel = accountLabel; self.plan = plan
        self.windows = windows; self.fetchedAt = fetchedAt; self.status = status; self.detail = detail
    }
}
