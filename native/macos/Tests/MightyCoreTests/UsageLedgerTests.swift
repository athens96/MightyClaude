import Foundation
import Testing
@testable import MightyCore

/// 사용량 집계 (`UsageLedger.swift`): run and session scopes, totals, the daily budget, the file.
struct UsageLedgerTests {
    private let calendar: Calendar = {
        var calendar = Calendar(identifier: .gregorian); calendar.timeZone = TimeZone(identifier: "Asia/Seoul")!; return calendar
    }()
    private func date(_ day: Int) -> Date { calendar.date(from: DateComponents(year: 2026, month: 11, day: day, hour: 12))! }
    private func usage(cost: Double?, tokens: Int?, scope: String, session: String = "s1") -> SessionUsage {
        SessionUsage(provider: "claude", source: "test", tokenScope: scope, model: "opus", providerSessionId: session, totalTokens: tokens, costUSD: cost, costScope: cost == nil ? nil : scope)
    }

    @Test func aSessionTotalCountsOnlyWhatGrew() {
        var ledger = UsageLedger()
        ledger.record(paneId: "p", workspaceId: "w", provider: "claude", model: "m", usage: usage(cost: 0.5, tokens: 1000, scope: "session"), at: date(9))
        ledger.record(paneId: "p", workspaceId: "w", provider: "claude", model: "m", usage: usage(cost: 0.8, tokens: 1500, scope: "session"), at: date(9))
        // The same total again adds nothing; a smaller one is a new session.
        #expect(ledger.record(paneId: "p", workspaceId: "w", provider: "claude", model: "m", usage: usage(cost: 0.8, tokens: 1500, scope: "session"), at: date(9)) == nil)
        ledger.record(paneId: "p", workspaceId: "w", provider: "claude", model: "m", usage: usage(cost: 0.1, tokens: 100, scope: "session"), at: date(9))
        // Another provider session id starts from zero.
        ledger.record(paneId: "p", workspaceId: "w", provider: "claude", model: "m", usage: usage(cost: 0.2, tokens: 50, scope: "session", session: "s2"), at: date(9))
        #expect(ledger.records.map(\.costUSD).map { ($0 * 100).rounded() / 100 } == [0.5, 0.3, 0.1, 0.2])
        #expect(ledger.records.map(\.tokens) == [1000, 500, 100, 50])
        #expect(ledger.records.allSatisfy { $0.model == "opus" && $0.day == UsageLedger.day(date(9)) })
    }

    @Test func runScopeNumbersAddUpAndTotalsGroup() {
        var ledger = UsageLedger()
        ledger.record(paneId: "a", workspaceId: "w1", provider: "codex", model: "gpt", usage: usage(cost: nil, tokens: 300, scope: "run"), at: date(8))
        ledger.record(paneId: "a", workspaceId: "w1", provider: "codex", model: "gpt", usage: usage(cost: nil, tokens: 300, scope: "run"), at: date(9))
        ledger.record(paneId: "b", workspaceId: "w2", provider: "claude", model: "m", usage: usage(cost: 1.0, tokens: 10, scope: "run"), at: date(9))
        let byWorkspace = UsageLedger.totals(ledger.records, by: \.workspaceId)
        #expect(byWorkspace.map(\.key) == ["w2", "w1"] && byWorkspace[1].tokens == 600 && byWorkspace[1].runs == 2)
        let daily = ledger.daily(days: 3, today: date(9), calendar: calendar)
        #expect(daily.map(\.key) == ["2026-11-07", "2026-11-08", "2026-11-09"])
        #expect(daily.map(\.tokens) == [0, 300, 310])
        #expect(ledger.cost(on: date(9), calendar: calendar) == 1.0)
    }

    @Test func theBudgetIsCrossedOnce() throws {
        var ledger = UsageLedger()
        let firstRecord = ledger.record(paneId: "p", workspaceId: "w", provider: "claude", model: "m", usage: usage(cost: 4, tokens: nil, scope: "run"), at: date(9))
        let first = try #require(firstRecord)
        #expect(!ledger.crossedBudget(5, after: first))
        let secondRecord = ledger.record(paneId: "p", workspaceId: "w", provider: "claude", model: "m", usage: usage(cost: 2, tokens: nil, scope: "run"), at: date(9))
        let second = try #require(secondRecord)
        #expect(ledger.crossedBudget(5, after: second))
        let thirdRecord = ledger.record(paneId: "p", workspaceId: "w", provider: "claude", model: "m", usage: usage(cost: 1, tokens: nil, scope: "run"), at: date(9))
        let third = try #require(thirdRecord)
        #expect(!ledger.crossedBudget(5, after: third) && !ledger.crossedBudget(0, after: third))
    }

    @Test func theFileKeepsRecentRecordsAndLastTotals() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-usage-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        var ledger = UsageLedger(records: [UsageRecord(day: "2020-01-01", workspaceId: "w", provider: "claude", model: "m", costUSD: 1, tokens: 1)])
        ledger.record(paneId: "p", workspaceId: "w", provider: "claude", model: "m", usage: usage(cost: 0.5, tokens: 5, scope: "session"), at: date(9))
        try ledger.save(directory: folder, today: date(9))
        let loaded = UsageLedger.load(directory: folder)
        #expect(loaded.records.count == 1 && loaded.last["p"]?.costUSD == 0.5)
        #expect(UsageLedger.load(directory: folder.appendingPathComponent("none")) == UsageLedger())
    }
}
