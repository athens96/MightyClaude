import Foundation
import MightyCore

/// Compact islands show at most two providers actually represented by open AI
/// sessions. Pick one whole host/account record per provider: active session,
/// running session, then latest submitted request (creation time as fallback).
/// Ties prefer local, then stable IDs. Never combine different hosts' windows,
/// login identities or counts. The details view continues to use raw accounts.
enum IslandSummaryPolicy {
    static func representatives(accounts: [IslandAccount], snapshot: AppSnapshot) -> [IslandAccount] {
        struct Candidate {
            let account: IslandAccount
            let priority: Int
            let recent: Date
        }
        let workspaces = Dictionary(snapshot.workspaces.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        let records = Dictionary(accounts.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        let candidates = snapshot.sessions.compactMap { session -> Candidate? in
            guard session.kind != "shell", ProviderOptions.ids.contains(session.provider),
                  let workspace = workspaces[session.workspaceId],
                  let account = records[session.provider + "|" + (workspace.remote?.connectionId ?? "local")] else { return nil }
            let active = session.id == snapshot.activeSessionId
            let running = ["starting", "running", "waiting", "stopping"].contains(session.status)
            let requested = session.runTiming?.startedAt
                ?? session.logs.last(where: { $0.kind == "user" }).flatMap { date($0.timestamp) }
                ?? date(session.createdAt) ?? .distantPast
            return Candidate(account: account, priority: active ? 0 : running ? 1 : 2, recent: requested)
        }.sorted { lhs, rhs in
            if lhs.priority != rhs.priority { return lhs.priority < rhs.priority }
            if lhs.recent != rhs.recent { return lhs.recent > rhs.recent }
            if lhs.account.remote != rhs.account.remote { return !lhs.account.remote }
            return lhs.account.id < rhs.account.id
        }
        var seen = Set<String>()
        return Array(candidates.compactMap { seen.insert($0.account.provider).inserted ? $0.account : nil }.prefix(2))
    }

    /// Selection and presentation are separate: retain the two highest measured
    /// percentages, then show session → weekly → other. Unknown is never zero.
    static func windows(_ usage: AccountUsageSnapshot?) -> [AccountUsageWindow] {
        let ranked = (usage?.windows ?? []).enumerated().filter {
            $0.element.usedPercent.isFinite && $0.element.usedPercent >= 0 && $0.element.usedPercent < 1_000_000
        }.sorted { lhs, rhs in
            if lhs.element.usedPercent != rhs.element.usedPercent { return lhs.element.usedPercent > rhs.element.usedPercent }
            if category(lhs.element.kind) != category(rhs.element.kind) { return category(lhs.element.kind) < category(rhs.element.kind) }
            return lhs.offset < rhs.offset
        }.prefix(2)
        return ranked.sorted { lhs, rhs in
            if category(lhs.element.kind) != category(rhs.element.kind) { return category(lhs.element.kind) < category(rhs.element.kind) }
            return lhs.offset < rhs.offset
        }.map(\.element)
    }

    static func lines(_ account: IslandAccount) -> [String] {
        let values = windows(account.usage)
        guard !values.isEmpty else { return ["계정 한도 —"] }
        return values.map { "\(quotaWindowLabel($0.kind)) \(Int($0.usedPercent.rounded()))% 사용" }
    }
    private static func category(_ kind: String) -> Int {
        switch kind {
        case "session", "five_hour", "primary": 0
        case "weekly", "seven_day", "secondary": 1
        default: 2
        }
    }
    private static func date(_ value: String) -> Date? {
        let formatter = ISO8601DateFormatter()
        if let result = formatter.date(from: value) { return result }
        formatter.formatOptions.insert(.withFractionalSeconds)
        return formatter.date(from: value)
    }
}
