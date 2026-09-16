import Foundation

/// One submitted request, including preparation and permission waits. The
/// session owns this clock; UI/pet state is only a projection of the session.
public struct AgentRunTiming: Codable, Equatable, Sendable {
    public let startedAt: Date
    public private(set) var lastObservedAt: Date
    public private(set) var finishedAt: Date?
    public private(set) var isApproximate: Bool

    public init(startedAt: Date = Date(), lastObservedAt: Date? = nil, finishedAt: Date? = nil, isApproximate: Bool = false) {
        self.startedAt = startedAt
        let observed = max(startedAt, lastObservedAt ?? finishedAt ?? startedAt)
        self.lastObservedAt = observed
        self.finishedAt = finishedAt.map { max(startedAt, observed, $0) }
        self.isApproximate = isApproximate
    }

    public mutating func observe(at date: Date = Date()) {
        guard finishedAt == nil, date.timeIntervalSince1970.isFinite else { return }
        lastObservedAt = max(startedAt, lastObservedAt, date)
    }

    public mutating func finish(at date: Date = Date()) {
        guard finishedAt == nil, date.timeIntervalSince1970.isFinite else { return }
        observe(at: date)
        finishedAt = lastObservedAt
    }

    /// A saved active process cannot survive an application restart. Freeze at
    /// its last event/save checkpoint, never at the time the app is reopened.
    public mutating func interrupt() {
        guard finishedAt == nil else { return }
        finishedAt = lastObservedAt
        isApproximate = true
    }

    public var isValid: Bool {
        startedAt.timeIntervalSince1970.isFinite && lastObservedAt.timeIntervalSince1970.isFinite &&
            lastObservedAt >= startedAt && (finishedAt.map { $0.timeIntervalSince1970.isFinite && $0 >= lastObservedAt } ?? true)
    }

    public func elapsed(at date: Date = Date()) -> TimeInterval {
        guard isValid else { return 0 }
        let value = (finishedAt ?? date).timeIntervalSince(startedAt)
        return value.isFinite ? max(0, value) : 0
    }

    public func label(at date: Date = Date()) -> String {
        let seconds = Int(min(elapsed(at: date), Double(Int.max / 2)))
        let duration = seconds >= 3_600 ? "\(seconds / 3_600):" + String(format: "%02d:%02d", seconds / 60 % 60, seconds % 60) : String(format: "%02d:%02d", seconds / 60, seconds % 60)
        return isApproximate ? "약 \(duration)" : duration
    }

    /// Old versions saved MightyClaude conversation timestamps but no timer.
    /// Require a last user request and a later response/activity. Configuration
    /// messages and workspace creation time are not evidence of run duration.
    public static func inferred(from logs: [LogEntry], running: Bool = false) -> AgentRunTiming? {
        guard let index = logs.lastIndex(where: { $0.kind == "user" }), let start = parseTimestamp(logs[index].timestamp) else { return nil }
        let dates = logs.dropFirst(index + 1).compactMap { entry -> Date? in
            guard ["assistant", "output", "error"].contains(entry.kind) || entry.activity != nil,
                  let date = parseTimestamp(entry.timestamp), date > start else { return nil }
            return date
        }
        guard let end = dates.max() else { return nil }
        return AgentRunTiming(startedAt: start, lastObservedAt: end, finishedAt: running ? nil : end, isApproximate: true)
    }

    public static func parseTimestamp(_ value: String) -> Date? {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: value), date.timeIntervalSince1970.isFinite { return date }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: value).flatMap { $0.timeIntervalSince1970.isFinite ? $0 : nil }
    }

    private static func timestamp(_ date: Date) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: date)
    }
    enum CodingKeys: String, CodingKey { case startedAt, lastObservedAt, finishedAt, isApproximate }
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        func date(_ key: CodingKeys, optional: Bool = false) throws -> Date? {
            guard let text = try c.decodeIfPresent(String.self, forKey: key) else {
                if optional { return nil }
                throw DecodingError.dataCorruptedError(forKey: key, in: c, debugDescription: "Missing run timestamp")
            }
            guard let value = Self.parseTimestamp(text) else { throw DecodingError.dataCorruptedError(forKey: key, in: c, debugDescription: "Invalid run timestamp") }
            return value
        }
        guard let start = try date(.startedAt) else { throw MightyError("Invalid run start") }
        let finish = try date(.finishedAt, optional: true)
        let observed = try date(.lastObservedAt, optional: true) ?? finish ?? start
        guard observed >= start, finish.map({ $0 >= observed }) ?? true else { throw DecodingError.dataCorruptedError(forKey: .lastObservedAt, in: c, debugDescription: "Run timestamps are out of order") }
        self.init(startedAt: start, lastObservedAt: observed, finishedAt: finish, isApproximate: try c.decodeIfPresent(Bool.self, forKey: .isApproximate) ?? false)
    }
    public func encode(to encoder: Encoder) throws {
        guard isValid else { throw EncodingError.invalidValue(self, .init(codingPath: encoder.codingPath, debugDescription: "Invalid run timestamp")) }
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(Self.timestamp(startedAt), forKey: .startedAt)
        try c.encode(Self.timestamp(lastObservedAt), forKey: .lastObservedAt)
        if let finishedAt { try c.encode(Self.timestamp(finishedAt), forKey: .finishedAt) }
        try c.encode(isApproximate, forKey: .isApproximate)
    }
}

public extension RunSession {
    mutating func beginRunTiming(at date: Date = Date()) {
        guard kind != "shell" else { return }
        runTiming = AgentRunTiming(startedAt: date)
    }

    /// Shared local/remote and provider-independent event path. Tool completion
    /// is only an observation; terminal run status alone finishes the clock.
    mutating func recordRunTiming(_ event: RunEvent, at date: Date = Date()) {
        guard kind != "shell", event.sessionId == id else { return }
        if event.type == "status", event.status == "running" {
            if runTiming == nil || runTiming?.finishedAt != nil { beginRunTiming(at: date) }
            runTiming?.observe(at: date)
        } else if event.type == "status", let status = event.status, ["completed", "error", "stopped"].contains(status) {
            runTiming?.finish(at: date)
        } else if status == "running" {
            runTiming?.observe(at: date)
        }
    }
}
