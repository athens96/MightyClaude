import Foundation
import Testing
import Darwin
@testable import MightyCore

private final class AccountTestClock: @unchecked Sendable {
    private let lock = NSLock()
    private var value = Date(timeIntervalSince1970: 1_800_000_000)
    func read() -> Date { lock.lock(); defer { lock.unlock() }; return value }
    func advance(_ seconds: Double) { lock.lock(); value = value.addingTimeInterval(seconds); lock.unlock() }
}
private actor AccountTestProbe {
    var calls = 0
    func read(_ provider: String) throws -> AccountUsageSnapshot {
        calls += 1
        if calls == 2 { throw AccountUsageFailure.rateLimited(120) }
        return AccountUsageSnapshot(provider: provider, windows: [.init(kind: "session", usedPercent: 25)], status: "available")
    }
    func count() -> Int { calls }
    func returnFixture(_ value: AccountUsageSnapshot) -> AccountUsageSnapshot { calls += 1; return value }
}
private actor AccountTestHTTP {
    var paths: [String] = []
    func read(_ request: URLRequest) throws -> AccountUsageHTTPResponse {
        #expect(request.httpMethod == "GET")
        #expect(request.url?.host == "api.anthropic.com")
        #expect(request.value(forHTTPHeaderField: "Authorization") == "Bearer fixture-token")
        #expect(request.value(forHTTPHeaderField: "anthropic-beta") == "oauth-2025-04-20")
        let path = request.url!.path; paths.append(path)
        let body = path.hasSuffix("usage") ? #"{"five_hour":{"utilization":21.5,"resets_at":"2026-09-16T20:00:00.000Z"},"seven_day":{"utilization":60}}"# : #"{"account":{"email":"fixture@example.test"},"organization":{"rate_limit_tier":"max"}}"#
        return AccountUsageHTTPResponse(status: 200, data: Data(body.utf8))
    }
}

struct AccountUsageTests {
    @Test func codexUsesReportedWindowsAndDoesNotInventWeeklyPeriod() throws {
        let account: [String: Any] = ["account": ["type": "chatgpt", "email": "fixture@example.test", "planType": "pro"]]
        let value = try AccountUsageService.mapCodex(account: account, limits: ["rateLimits": ["primary": ["usedPercent": 99]], "rateLimitsByLimitId": ["codex": ["primary": ["usedPercent": 25, "windowDurationMins": 300, "resetsAt": 1_800_000_000], "secondary": ["usedPercent": 44, "windowDurationMins": 10080]]]])
        #expect(value.windows.map(\.kind) == ["session", "weekly"])
        #expect(value.windows[0].usedPercent == 25 && value.windows[0].resetsAt != nil)
        #expect(value.accountLabel == "fixture@example.test" && value.plan == "pro")
        let unusual = try AccountUsageService.mapCodex(account: account, limits: ["rateLimits": ["primary": ["usedPercent": 10, "windowDurationMins": 15], "secondary": ["usedPercent": true]]])
        #expect(unusual.windows.map(\.kind) == ["15m"])
        #expect(throws: AccountUsageFailure.self) { try AccountUsageService.mapCodex(account: ["account": ["type": "apiKey"]], limits: [:]) }
    }

    @Test func claudeReadsOnlyQuotaAndProfileWithInjectedCredentials() async throws {
        let http = AccountTestHTTP()
        let result = try await AccountUsageService.claude(environment: [:], load: { ClaudeQuotaCredential(token: "fixture-token", plan: "pro") }, http: { try await http.read($0) })
        #expect(result.windows.map(\.kind) == ["session", "weekly"])
        #expect(result.windows.first?.usedPercent == 21.5)
        #expect(result.accountLabel == "fixture@example.test" && result.plan == "max")
        #expect(await http.paths == ["/api/oauth/usage", "/api/oauth/profile"])
        #expect(!String(decoding: try JSONEncoder().encode(result), as: UTF8.self).contains("fixture-token"))
    }

    @Test func claudeRejectsCustomAuthBeforeLoadingCredentialsAndValidatesScopes() async throws {
        await #expect(throws: AccountUsageFailure.self) {
            try await AccountUsageService.claude(environment: ["ANTHROPIC_BASE_URL": "https://custom.invalid"], load: {
                Issue.record("Custom authentication must not read default credentials"); return nil
            }, http: { _ in Issue.record("Custom authentication must not call production"); throw AccountUsageFailure.network })
        }
        for body in [#"{"claudeAiOauth":{"accessToken":"fake","scopes":["user:inference"]}}"#, #"{"claudeAiOauth":{"accessToken":"fake","expiresAt":1}}"#, #"{"claudeAiOauth":{"accessToken":"fake\nheader","scopes":["user:profile"]}}"#] {
            #expect(AccountUsageService.parseClaudeCredential(Data(body.utf8)) == nil)
        }
        #expect(AccountUsageService.parseClaudeCredential(Data(#"{"claudeAiOauth":{"accessToken":"fake","scopes":["user:profile"]}}"#.utf8))?.token == "fake")
    }

    @Test func malformedQuotaNeverBecomesZeroAndResetDatesAreValidated() {
        let value = AccountUsageService.mapClaude(["five_hour": ["utilization": true], "seven_day": ["utilization": -1], "seven_day_sonnet": ["utilization": Double.infinity]])
        #expect(value.windows.isEmpty && value.status == "unavailable")
        let zero = AccountUsageService.mapClaude(["five_hour": ["utilization": 0, "resets_at": "invalid"]])
        #expect(zero.windows.first?.usedPercent == 0 && zero.windows.first?.resetsAt == nil)
    }

    @Test func profileRateLimitPreservesFreshQuotaAndStillBlocksForcedRefresh() async throws {
        let value = try await AccountUsageService.claude(environment: [:], load: { ClaudeQuotaCredential(token: "fixture-token", plan: "pro") }, http: { request in
            if request.url?.path.hasSuffix("profile") == true { return AccountUsageHTTPResponse(status: 429, data: Data(), retryAfter: "120") }
            return AccountUsageHTTPResponse(status: 200, data: Data(#"{"five_hour":{"utilization":24}}"#.utf8))
        })
        #expect(value.status == "available" && value.windows.first?.usedPercent == 24)
        #expect(value.retryAfterSeconds == 120)
        let serialized = try JSONEncoder().encode(value)
        #expect((try JSONSerialization.jsonObject(with: serialized) as? [String: Any])?["retryAfterSeconds"] == nil)
        let clock = AccountTestClock(), probe = AccountTestProbe()
        let service = AccountUsageService(now: { clock.read() }, probe: { _ in await probe.returnFixture(value) })
        _ = await service.read(provider: "claude")
        clock.advance(61)
        #expect(await service.read(provider: "claude", force: true).windows.first?.usedPercent == 24)
        #expect(await probe.count() == 1)
        clock.advance(60)
        _ = await service.read(provider: "claude", force: true)
        #expect(await probe.count() == 2)
        await service.shutdown()
    }

    @Test func rateLimitCooldownKeepsLastGoodAndForceCannotBypassIt() async {
        let clock = AccountTestClock(), probe = AccountTestProbe()
        let service = AccountUsageService(now: { clock.read() }, probe: { try await probe.read($0) })
        let first = await service.read(provider: "claude")
        #expect(first.status == "available")
        #expect(await service.read(provider: "claude", force: true) == first)
        #expect(await probe.count() == 1)
        clock.advance(61)
        let stale = await service.read(provider: "claude")
        #expect(stale.status == "stale" && stale.windows == first.windows && stale.fetchedAt == first.fetchedAt)
        clock.advance(60)
        #expect(await service.read(provider: "claude", force: true) == stale)
        #expect(await probe.count() == 2)
        clock.advance(61)
        #expect(await service.read(provider: "claude").status == "available")
        #expect(await probe.count() == 3)
        await service.shutdown()
        #expect(await service.read(provider: "claude").status == "cancelled")
    }

    @Test func authenticationFailureClearsOldAccountInsteadOfLeakingIt() async {
        let clock = AccountTestClock()
        let service = AccountUsageService(now: { clock.read() }, probe: { _ in
            if clock.read().timeIntervalSince1970 > 1_800_000_060 { throw AccountUsageFailure.authentication }
            return AccountUsageSnapshot(provider: "claude", accountLabel: "old@example.test", windows: [.init(kind: "session", usedPercent: 50)], status: "available")
        })
        _ = await service.read(provider: "claude")
        clock.advance(61)
        let value = await service.read(provider: "claude")
        #expect(value.windows.isEmpty && value.accountLabel == nil && value.status == "unavailable")
        await service.shutdown()
    }

    @Test func codexFakeRPCReadsAccountWithoutStartingAnyThread() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-account-test-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let binary = root.appendingPathComponent("codex")
        let script = #"""
        #!/bin/sh
        read -r line
        printf '%s\n' "$line" >> "$CAPTURE"
        printf '%s\n' '{"id":1,"result":{}}'
        read -r line
        printf '%s\n' "$line" >> "$CAPTURE"
        read -r line
        printf '%s\n' "$line" >> "$CAPTURE"
        printf '%s\n' '{"id":2,"result":{"account":{"type":"chatgpt","email":"fake@example.test","planType":"pro"}}}'
        read -r line
        printf '%s\n' "$line" >> "$CAPTURE"
        printf '%s\n' '{"id":3,"result":{"rateLimits":{"primary":{"usedPercent":20,"windowDurationMins":300},"secondary":{"usedPercent":45,"windowDurationMins":10080}}}}'
        /bin/sleep 10
        """#
        try Data(script.utf8).write(to: binary)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
        let capture = root.appendingPathComponent("rpc")
        let value = try await AccountUsageService.probeCodex(command: ProviderCommand(provider: "codex", executable: binary, version: "fixture"), environment: ["CAPTURE": capture.path], timeout: 1)
        #expect(value.windows.map(\.usedPercent) == [20, 45])
        let lines = try String(contentsOf: capture, encoding: .utf8).split(separator: "\n")
        let methods = try lines.map { try JSONSerialization.jsonObject(with: Data($0.utf8)) as! [String: Any] }.compactMap { $0["method"] as? String }
        #expect(methods == ["initialize", "initialized", "account/read", "account/rateLimits/read"])
    }

    @Test func codexProbeTimeoutStopsTheFakeChild() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-account-timeout-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let binary = root.appendingPathComponent("codex"), pidFile = root.appendingPathComponent("pid")
        try Data("#!/bin/sh\nprintf '%s' \"$$\" > \"$PIDFILE\"\n/bin/sleep 10\n".utf8).write(to: binary)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
        await #expect(throws: AccountUsageFailure.self) {
            try await AccountUsageService.probeCodex(command: ProviderCommand(provider: "codex", executable: binary, version: "fixture"), environment: ["PIDFILE": pidFile.path], timeout: 1)
        }
        let pid = try #require(Int32(String(contentsOf: pidFile, encoding: .utf8)))
        #expect(Darwin.kill(pid, 0) != 0)
    }
}
