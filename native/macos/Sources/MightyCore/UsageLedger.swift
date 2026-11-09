import Foundation

/// One finished run's share of cost and tokens, as the 사용량 view sums them.
public struct UsageRecord: Codable, Sendable, Equatable {
    /// Local calendar day, `yyyy-MM-dd`.
    public var day: String
    public var workspaceId: String
    public var provider: String
    public var model: String
    public var costUSD: Double
    public var tokens: Int

    public init(day: String, workspaceId: String, provider: String, model: String, costUSD: Double, tokens: Int) {
        self.day = day; self.workspaceId = workspaceId; self.provider = provider; self.model = model; self.costUSD = costUSD; self.tokens = tokens
    }
}

/// 사용량 집계: each run's cost and tokens as the CLI reported them, kept per
/// day so the totals over days, workspaces and models can be read back, and a
/// daily budget can be watched. A CLI reports either the run's own numbers
/// (`run` scope) or the session's running total (`session` scope); a session
/// total counts only what grew since the session's last record, so a resumed
/// or repeated total is never counted twice.
public struct UsageLedger: Sendable, Equatable {
    public static let fileName = "usage-ledger.json"
    /// Records older than this many days are dropped when the ledger is saved.
    public static let keepDays = 120

    /// What one pane's session last reported, for the next session-scope delta.
    public struct Last: Codable, Sendable, Equatable {
        public var providerSessionId: String?
        public var costUSD: Double?
        public var tokens: Int?
    }

    public var records: [UsageRecord] = []
    /// By pane id.
    public var last: [String: Last] = [:]

    public init(records: [UsageRecord] = [], last: [String: Last] = [:]) { self.records = records; self.last = last }

    public static func day(_ date: Date, calendar: Calendar = .current) -> String {
        let parts = calendar.dateComponents([.year, .month, .day], from: date)
        return String(format: "%04d-%02d-%02d", parts.year ?? 0, parts.month ?? 0, parts.day ?? 0)
    }

    /// Adds what the pane's run that just ended used; returns the record, nil when it used nothing countable.
    @discardableResult
    public mutating func record(paneId: String, workspaceId: String, provider: String, model: String, usage: SessionUsage, at date: Date = Date()) -> UsageRecord? {
        let previous = last[paneId]
        let sameSession = previous != nil && previous?.providerSessionId == usage.providerSessionId
        func delta(_ value: Double?, scope: String?, before: Double?) -> Double {
            guard let value else { return 0 }
            guard scope == "session", sameSession, let before else { return value }
            // A smaller total is a new session under the same id: it all counts.
            return value >= before ? value - before : value
        }
        let cost = delta(usage.costUSD, scope: usage.costScope, before: previous?.costUSD)
        let tokens = Int(delta(usage.totalTokens.map(Double.init), scope: usage.tokenScope, before: previous?.tokens.map(Double.init)))
        last[paneId] = Last(providerSessionId: usage.providerSessionId, costUSD: usage.costUSD, tokens: usage.totalTokens)
        guard cost > 0 || tokens > 0 else { return nil }
        let entry = UsageRecord(day: Self.day(date), workspaceId: workspaceId, provider: provider,
                                model: usage.model ?? model, costUSD: cost, tokens: tokens)
        records.append(entry)
        return entry
    }

    /// A pane that closed reports nothing more.
    public mutating func forget(paneId: String) { last.removeValue(forKey: paneId) }

    // MARK: - Reading back

    public struct Total: Sendable, Equatable, Identifiable {
        public var id: String { key }
        public let key: String
        public var costUSD: Double
        public var tokens: Int
        public var runs: Int
    }

    /// Records from the last `days` days up to `today` (inclusive).
    public func recent(days: Int, today: Date = Date(), calendar: Calendar = .current) -> [UsageRecord] {
        guard let start = calendar.date(byAdding: .day, value: -(days - 1), to: today) else { return records }
        let first = Self.day(start, calendar: calendar)
        return records.filter { $0.day >= first }
    }

    /// Totals of `records` grouped by `key`, largest cost first (then tokens, then key).
    public static func totals(_ records: [UsageRecord], by key: (UsageRecord) -> String) -> [Total] {
        var sums: [String: Total] = [:]
        for record in records {
            let name = key(record)
            var total = sums[name] ?? Total(key: name, costUSD: 0, tokens: 0, runs: 0)
            total.costUSD += record.costUSD; total.tokens += record.tokens; total.runs += 1
            sums[name] = total
        }
        return sums.values.sorted { ($0.costUSD, $0.tokens, $1.key) > ($1.costUSD, $1.tokens, $0.key) }
    }

    /// One total per day of the last `days` days, oldest first, days without records included.
    public func daily(days: Int, today: Date = Date(), calendar: Calendar = .current) -> [Total] {
        let sums = Dictionary(uniqueKeysWithValues: Self.totals(recent(days: days, today: today, calendar: calendar), by: \.day).map { ($0.key, $0) })
        return (0..<days).reversed().compactMap { back in
            calendar.date(byAdding: .day, value: -back, to: today).map { Self.day($0, calendar: calendar) }
        }.map { sums[$0] ?? Total(key: $0, costUSD: 0, tokens: 0, runs: 0) }
    }

    /// Today's cost so far.
    public func cost(on date: Date = Date(), calendar: Calendar = .current) -> Double {
        let today = Self.day(date, calendar: calendar)
        return records.filter { $0.day == today }.reduce(0) { $0 + $1.costUSD }
    }

    /// Whether `record` took the day over `budget`: the day's cost was under it before and is not now.
    public func crossedBudget(_ budget: Double, after record: UsageRecord) -> Bool {
        guard budget > 0 else { return false }
        let day = records.filter { $0.day == record.day }.reduce(0) { $0 + $1.costUSD }
        return day >= budget && day - record.costUSD < budget
    }

    // MARK: - File

    public static func load(directory: URL) -> UsageLedger {
        guard let data = try? Data(contentsOf: directory.appendingPathComponent(fileName)),
              let ledger = try? JSONDecoder().decode(UsageLedger.self, from: data) else { return UsageLedger() }
        return ledger
    }

    public func save(directory: URL, today: Date = Date()) throws {
        var kept = self
        kept.records = recent(days: Self.keepDays, today: today)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try JSONEncoder().encode(kept).write(to: directory.appendingPathComponent(Self.fileName), options: .atomic)
    }
}

extension UsageLedger: Codable {}
