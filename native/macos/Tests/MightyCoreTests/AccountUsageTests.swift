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
        let url = request.url!; let path = url.path
        // Path plus the exact ordered query: a usage GET carries the CLI's own
        // variant or no query at all, and never an extra key.
        paths.append(path + (url.query.map { "?" + $0 } ?? ""))
        let body = path.hasSuffix("usage") ? #"{"five_hour":{"utilization":21.5,"resets_at":"2026-09-16T20:00:00.000Z"},"seven_day":{"utilization":60}}"# : #"{"account":{"email":"fixture@example.test"},"organization":{"rate_limit_tier":"max"}}"#
        return AccountUsageHTTPResponse(status: 200, data: Data(body.utf8))
    }
}

private actor AccountPermissionProbe {
    var calls = 0
    var interactiveCalls = 0
    func read(_ provider: String, interactive: Bool) throws -> AccountUsageSnapshot {
        calls += 1
        guard interactive else { throw AccountUsageFailure.keychainPermission }
        interactiveCalls += 1
        return AccountUsageSnapshot(provider: provider, windows: [.init(kind: "session", usedPercent: 12)], status: "available")
    }
    func count() -> Int { calls }
    func interactiveCount() -> Int { interactiveCalls }
}

struct AccountUsageTests {
    @Test func keychainPermissionStaysSilentUntilAnInteractiveRead() async throws {
        let clock = AccountTestClock(); let probe = AccountPermissionProbe()
        let service = AccountUsageService(now: { clock.read() }, interactiveProbe: { provider, interactive in try await probe.read(provider, interactive: interactive) })
        let first = await service.read(provider: "claude")
        #expect(first.status == "permission"); #expect(first.windows.isEmpty); #expect(first.detail.contains("Keychain"))
        // Automatic polling, even forced, must not retry for an hour or prompt.
        clock.advance(600)
        let polled = await service.read(provider: "claude", force: true)
        #expect(polled.status == "permission"); #expect(await probe.count() == 1)
        let granted = await service.read(provider: "claude", force: true, interactive: true)
        #expect(granted.status == "available"); #expect(granted.windows.first?.usedPercent == 12)
        #expect(await probe.count() == 2); #expect(await probe.interactiveCount() == 1)
        await service.shutdown()
    }

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
        #expect(await http.paths == ["/api/oauth/usage", "/api/oauth/profile",
                                     "/api/oauth/usage?cedar_ember=1&skip_spend=1",
                                     "/api/oauth/usage?at_wall=1&skip_spend=1"])
        // This fixture answers the entitlement queries with a body that has no
        // reset field at all — an account outside both programmes.
        #expect(result.resets.map(\.state) == ["ineligible", "ineligible"])
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
        // This case verifies the RPC exchange, not its latency. Parallel CI
        // process fixtures can consume the old one-second scheduling budget.
        let value = try await AccountUsageService.probeCodex(command: ProviderCommand(provider: "codex", executable: binary, version: "fixture"), environment: ["CAPTURE": capture.path], timeout: 5)
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

// ---------------------------------------------------------------- 리셋권 rows

/// One fixture pair: what each entitlement query answers and what the row must
/// become. `provenance` records where the shape came from — every field name
/// below is read from the installed CLI 2.1.280 binary with `strings`
/// (`cedar_ember`/`juniper_tide` blocks, their decoders and the two query
/// variants); only the field *values* are made up.
private struct ResetScenario: Sendable {
    var name: String
    var provenance = "binary-derived (claude 2.1.280 decoder field names)"
    var cedarStatus = 200
    var cedar: String
    var juniperStatus = 200
    var juniper: String
    var cedarState: String
    var juniperState: String
}

private actor ResetFixtureHTTP {
    private var scenario: ResetScenario?
    var trace: [String] = []
    var postCount = 0
    private let usage = #"{"five_hour":{"utilization":21.5},"seven_day":{"utilization":60}}"#
    private let profile = #"{"account":{"email":"fixture@example.test"},"organization":{"rate_limit_tier":"max"}}"#

    func use(_ value: ResetScenario) { scenario = value }
    func calls() -> [String] { trace }
    func posts() -> Int { postCount }

    func read(_ request: URLRequest) throws -> AccountUsageHTTPResponse {
        // No POST exists in this app; a fake transport that ever saw one fails.
        if request.httpMethod != "GET" { postCount += 1 }
        #expect(request.url?.host == "api.anthropic.com")
        #expect(request.value(forHTTPHeaderField: "Authorization") == "Bearer fixture-token")
        let url = request.url!
        trace.append(url.path + (url.query.map { "?" + $0 } ?? ""))
        guard let scenario else { throw AccountUsageFailure.network }
        switch url.query {
        case "cedar_ember=1&skip_spend=1":
            #expect(request.timeoutInterval == 5)
            return AccountUsageHTTPResponse(status: scenario.cedarStatus, data: Data(scenario.cedar.utf8))
        case "at_wall=1&skip_spend=1":
            #expect(request.timeoutInterval == 5)
            return AccountUsageHTTPResponse(status: scenario.juniperStatus, data: Data(scenario.juniper.utf8))
        default:
            return AccountUsageHTTPResponse(status: 200, data: Data((url.path.hasSuffix("profile") ? profile : usage).utf8))
        }
    }
}

private func iso(_ seconds: Double) -> String { AccountUsageService.timestamp(Date(timeIntervalSince1970: seconds)) }

/// A cedar_ember grant, the CLI decoder's field names.
private func grant(left: Int, ends: Double, usableNow: Bool, requiresLimit: Bool, paused: Bool = false) -> String {
    """
    {"id":"grant-fixture","label":"fixture","resets_total":5,"resets_left":\(left),\
    "starts_at":"\(iso(1_799_900_000))","ends_at":"\(iso(ends))","clears":[],\
    "paused":\(paused),"usable_now":\(usableNow),"use_requires_limit":\(requiresLimit),"percent_used":{}}
    """
}
private func cedar(_ grantJSON: String?, atLimit: Bool, cooldown: Double?, exhausted: String = "[]", eligible: Bool = true, reason: String? = nil) -> String {
    let selected = grantJSON == nil ? "null" : "\"grant-fixture\""
    let reasonJSON = reason.map { "\"\($0)\"" } ?? "null"
    return """
    {"cedar_ember":{"eligible":\(eligible),"ineligible_reason":\(reasonJSON),"at_limit":\(atLimit),\
    "exhausted":\(exhausted),"grants":[\(grantJSON ?? "")],"next_grant_id":\(selected),\
    "weekly_resets_at":null,"cooldown_until":\(cooldown.map { "\"\(iso($0))\"" } ?? "null")}}
    """
}
private func juniper(inExperiment: Bool = true, available: Bool = false, next: Double?, perWeek: Int, reason: String? = nil) -> String {
    """
    {"juniper_tide":{"in_experiment":\(inExperiment),"ineligible_reason":\(reason.map { "\"\($0)\"" } ?? "null"),\
    "available":\(available),"next_available_at":\(next.map { "\"\(iso($0))\"" } ?? "null"),\
    "weekly_resets_at":"\(iso(1_800_600_000))","resets_per_week":\(perWeek),"tenure_bucket":"established",\
    "billing_path":"subscription","billing_period":"monthly","extra_usage_state":"off"}}
    """
}

struct AccountResetEntitlementTests {
    private static let origin: Double = 1_800_000_000
    private static let future = origin + 86_400
    private static let past = origin - 86_400

    /// The seven states, named one by one: available, held, cooldown,
    /// exhausted, none, ineligible, unknown. Every row is produced by a real
    /// AccountUsageService instance reading fixture HTTP through its own seam,
    /// with a fixture clock the test advances and never sleeps on.
    @Test func usageResetRendersSevenStatesFromTheSharedKeys() async throws {
        let clock = AccountTestClock(), http = ResetFixtureHTTP()
        let service = AccountUsageService(now: { clock.read() }, probe: { _ in
            try await AccountUsageService.claude(environment: [:],
                load: { ClaudeQuotaCredential(token: "fixture-token", plan: "pro") },
                http: { try await http.read($0) }, now: { clock.read() })
        })
        let origin = Self.origin, future = Self.future, past = Self.past

        let scenarios: [ResetScenario] = [
            // available: the granted reset is usable now; the at-wall reset too.
            .init(name: "available",
                  cedar: cedar(grant(left: 3, ends: future, usableNow: true, requiresLimit: false), atLimit: true, cooldown: nil),
                  juniper: juniper(available: true, next: nil, perWeek: 2),
                  cedarState: "available", juniperState: "available"),
            // held: the grant waits for the account to reach its limit.
            // cooldown (at-wall): a next time still ahead of the clock.
            .init(name: "held",
                  cedar: cedar(grant(left: 2, ends: future, usableNow: false, requiresLimit: true), atLimit: false, cooldown: nil),
                  juniper: juniper(next: future, perWeek: 2),
                  cedarState: "held", juniperState: "cooldown"),
            // cooldown (granted): still cooling down from the last use.
            // none (at-wall): this account gets none per week.
            .init(name: "cooldown",
                  cedar: cedar(grant(left: 1, ends: future, usableNow: false, requiresLimit: false), atLimit: true, cooldown: future),
                  juniper: juniper(next: past, perWeek: 0),
                  cedarState: "cooldown", juniperState: "none"),
            // exhausted: nothing left in this period, on either programme.
            .init(name: "exhausted",
                  cedar: cedar(grant(left: 0, ends: future, usableNow: false, requiresLimit: false), atLimit: true, cooldown: past, exhausted: "[\"spent\"]"),
                  juniper: juniper(next: past, perWeek: 3),
                  cedarState: "exhausted", juniperState: "exhausted"),
            // none: no grant is selected at all.
            .init(name: "none",
                  cedar: cedar(nil, atLimit: false, cooldown: nil),
                  juniper: juniper(next: nil, perWeek: 0),
                  cedarState: "none", juniperState: "none"),
            // ineligible: the server named a reason, and a 404 says the same.
            .init(name: "ineligible",
                  cedar: cedar(nil, atLimit: false, cooldown: nil, eligible: false, reason: "not_in_experiment"),
                  juniperStatus: 404, juniper: "",
                  cedarState: "ineligible", juniperState: "ineligible"),
            // unknown: a 5xx, and a block this app cannot read. The copy blames
            // this app's connection, never Anthropic's policy.
            .init(name: "unknown", provenance: "assumed (transport failure shapes)",
                  cedarStatus: 503, cedar: "",
                  juniper: #"{"juniper_tide":42}"#,
                  cedarState: "unknown", juniperState: "unknown"),
        ]

        var seen = Set<String>()
        for scenario in scenarios {
            await http.use(scenario)
            clock.advance(61)
            let snapshot = await service.read(provider: "claude", force: true)
            // A reset read never disturbs the base usage windows or the status.
            #expect(snapshot.status == "available")
            #expect(snapshot.windows.map(\.kind) == ["session", "weekly"])
            #expect(snapshot.resets.map(\.program) == ["cedar_ember", "juniper_tide"])
            #expect(!scenario.provenance.isEmpty)

            let granted = try #require(snapshot.resets.first { $0.program == "cedar_ember" })
            let atWall = try #require(snapshot.resets.first { $0.program == "juniper_tide" })
            #expect(granted.state == scenario.cedarState, "cedar_ember in \(scenario.name)")
            #expect(atWall.state == scenario.juniperState, "juniper_tide in \(scenario.name)")
            seen.insert(granted.state); seen.insert(atWall.state)

            for row in snapshot.resets {
                // Exactly one shared usage.reset.* key per program and state,
                // and a line that actually resolved through the catalogue.
                #expect(row.copyKey.hasPrefix("usage.reset."))
                #expect(row.line != row.copyKey && !row.line.isEmpty)
                #expect(!row.label.isEmpty)
                // The link is enabled in every one of the seven states.
                #expect(AccountResetEntitlement.linkKey == "usage.reset.link")
                #expect(!AccountResetEntitlement.linkLabel.isEmpty)
                #expect(AccountResetEntitlement.linkTarget.hasPrefix("https://claude.ai/"))
            }

            switch scenario.name {
            case "available":
                // The granted line carries the remaining count and the expiry.
                #expect(granted.copyKey == "usage.reset.available.cedarEmber")
                #expect(granted.remainingCount == 3 && granted.expiresAt == iso(future))
                #expect(granted.line.contains("3") && granted.line.contains(AccountResetEntitlement.day(granted.expiresAt)))
                // The at-wall line says it is available now.
                #expect(atWall.copyKey == "usage.reset.available.juniperTide")
                #expect(atWall.resetsPerWeek == 2)
            case "held":
                #expect(granted.copyKey == "usage.reset.held")
                // The at-wall line carries the next time and the weekly count.
                #expect(atWall.copyKey == "usage.reset.cooldown.juniperTide")
                #expect(atWall.nextAvailableAt == iso(future) && atWall.resetsPerWeek == 2)
                #expect(atWall.line.contains("2") && atWall.line.contains(AccountResetEntitlement.moment(atWall.nextAvailableAt)))
            case "cooldown":
                #expect(granted.copyKey == "usage.reset.cooldown.cedarEmber")
                #expect(granted.nextAvailableAt == iso(future))
                #expect(atWall.copyKey == "usage.reset.none")
            case "exhausted": #expect(granted.copyKey == "usage.reset.exhausted" && atWall.copyKey == "usage.reset.exhausted")
            case "none": #expect(granted.copyKey == "usage.reset.none" && atWall.copyKey == "usage.reset.none")
            case "ineligible": #expect(granted.copyKey == "usage.reset.ineligible" && atWall.copyKey == "usage.reset.ineligible")
            default: #expect(granted.copyKey == "usage.reset.unknown" && atWall.copyKey == "usage.reset.unknown")
            }
        }
        #expect(seen == ["available", "held", "cooldown", "exhausted", "none", "ineligible", "unknown"])

        // The clock, not the wall time, decides when a grant window has closed.
        let closing = clock.read().timeIntervalSince1970 + 200
        await http.use(.init(name: "expiry",
                             cedar: cedar(grant(left: 4, ends: closing, usableNow: true, requiresLimit: false), atLimit: true, cooldown: nil),
                             juniper: juniper(available: true, next: nil, perWeek: 1),
                             cedarState: "available", juniperState: "available"))
        clock.advance(61)
        #expect(await service.read(provider: "claude", force: true).resets.first?.state == "available")
        clock.advance(183)
        #expect(await service.read(provider: "claude", force: true).resets.first?.state == "none")

        // Every call was a GET on the allowed paths, with the query the CLI uses.
        #expect(await http.posts() == 0)
        #expect(Set(await http.calls()) == ["/api/oauth/usage", "/api/oauth/profile",
                                            "/api/oauth/usage?cedar_ember=1&skip_spend=1",
                                            "/api/oauth/usage?at_wall=1&skip_spend=1"])
        // No token, account or grant id reaches the presentation data.
        let encoded = String(decoding: try JSONEncoder().encode(await service.read(provider: "claude")), as: UTF8.self)
        #expect(!encoded.contains("fixture-token") && !encoded.contains("grant-fixture"))
        await service.shutdown()
    }

    /// The rows do not exist while the direct-lookup switch is off, and the
    /// switch is off out of the box.
    @Test func usageResetRowsAreHiddenWhileDirectLookupIsOff() {
        let snapshot = AccountUsageSnapshot(provider: "claude", windows: [.init(kind: "session", usedPercent: 10)],
                                            resets: [AccountResetEntitlement(program: .cedarEmber, state: .available, remainingCount: 2)],
                                            status: "available")
        #expect(AccountResetPresentation.rows(snapshot, directLookupEnabled: false).isEmpty)
        #expect(AccountResetPresentation.rows(nil, directLookupEnabled: false).isEmpty)
        let shown = AccountResetPresentation.rows(snapshot, directLookupEnabled: true)
        #expect(shown.map(\.program) == ["cedar_ember", "juniper_tide"])
        #expect(shown[0].state == "available" && shown[1].state == "unknown")
        // A relaunch with nothing read yet is unknown, never a stale number.
        #expect(AccountResetPresentation.rows(nil, directLookupEnabled: true).allSatisfy { $0.state == "unknown" })
    }

    /// The smoke the macOS app runs under --usage-reset-smoke-test, driven here
    /// through the same Core entry point: an injected AccountUsageService built
    /// on a fixture clock and a fake transport renders the "available" and
    /// "unknown" 리셋권 rows, every request is a GET on the allow-list with
    /// skip_spend=1, and fakeTransportPostCount is 0.
    @Test func usageResetSmokeRendersAvailableAndUnknownFromAnInjectedService() async throws {
        let result = await AccountResetSmoke.run()
        #expect(result.cedarEmberState == "available")
        #expect(result.juniperTideState == "unknown")
        #expect(result.fakeTransportPostCount == 0)
        #expect(result.requests == ["GET /api/oauth/usage", "GET /api/oauth/profile",
                                    "GET /api/oauth/usage?cedar_ember=1&skip_spend=1",
                                    "GET /api/oauth/usage?at_wall=1&skip_spend=1"])
        // Every rendered line resolved from a shared usage.reset.* key.
        #expect(result.lines.count == 2)
        #expect(result.lines.allSatisfy { !$0.isEmpty && !$0.hasPrefix("usage.reset.") })
        #expect(result.passed)
        // Nothing the smoke records carries a credential.
        let encoded = String(decoding: try JSONEncoder().encode(result), as: UTF8.self)
        #expect(!encoded.contains("smoke-fixture-not-a-real-token") && !encoded.contains("grant-fixture"))
    }
}
