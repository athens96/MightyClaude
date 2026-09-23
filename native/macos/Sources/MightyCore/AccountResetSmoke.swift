import Foundation

/// What the 리셋권 smoke records. The count of POSTs the fake transport saw is
/// part of the contract because no POST exists in this app at all: the
/// entitlement surface is read with GET and nothing is ever written back.
public struct AccountResetSmokeResult: Codable, Equatable, Sendable {
    public var passed: Bool
    /// Always 0. A non-GET reaching the transport fails the smoke.
    public var fakeTransportPostCount: Int
    public var cedarEmberState: String
    public var juniperTideState: String
    /// Method + path + exact ordered query of every request the transport saw.
    public var requests: [String]
    /// The rendered lines, so a smoke that resolved no locale key cannot pass.
    public var lines: [String]
}

/// The fake transport the smoke injects: GET only, one fixture reply per
/// endpoint, and a running count of anything that is not a GET.
public final class AccountResetSmokeTransport: @unchecked Sendable {
    private let lock = NSLock()
    private var seen: [String] = []
    private var posts = 0

    public init() {}

    public var requests: [String] { lock.lock(); defer { lock.unlock() }; return seen }
    public var postCount: Int { lock.lock(); defer { lock.unlock() }; return posts }

    func respond(_ request: URLRequest) throws -> AccountUsageHTTPResponse {
        let method = request.httpMethod ?? ""
        let url = request.url!
        let target = url.path + (url.query.map { "?" + $0 } ?? "")
        lock.lock(); seen.append(method + " " + target); if method != "GET" { posts += 1 }; lock.unlock()
        guard method == "GET", url.host == "api.anthropic.com" else { throw AccountUsageFailure.network }
        switch url.query {
        case ResetProgram.cedarEmber.query:
            return AccountUsageHTTPResponse(status: 200, data: Data(AccountResetSmoke.cedarAvailableBody.utf8))
        // A 5xx on an entitlement GET leaves that row unknown and never
        // disturbs the base usage windows.
        case ResetProgram.juniperTide.query:
            return AccountUsageHTTPResponse(status: 503, data: Data())
        default:
            let body = url.path.hasSuffix("profile") ? "{}" : AccountResetSmoke.usageBody
            return AccountUsageHTTPResponse(status: 200, data: Data(body.utf8))
        }
    }
}

/// The 리셋권 smoke, shared by the macOS app's `--usage-reset-smoke-test` run
/// and by the test that proves it. An AccountUsageService is injected with a
/// fixture clock and the fake transport above, so the rows render from fixture
/// data irrespective of the direct-lookup switch and without writing any
/// preference. cedar_ember renders `available`, juniper_tide renders `unknown`.
public enum AccountResetSmoke {
    /// Binary-derived shape (CLI 2.1.280 decoder field names); the values are
    /// a fixture. The grant's window closes well after the fixture clock.
    public static let cedarAvailableBody = """
    {"cedar_ember":{"eligible":true,"in_experiment":true,"ineligible_reason":null,\
    "next_grant_id":"grant-fixture","grants":[{"id":"grant-fixture","resets_total":3,\
    "resets_left":2,"starts_at":"2026-09-01T00:00:00Z","ends_at":"2026-12-31T00:00:00Z",\
    "usable_now":true,"use_requires_limit":false,"paused":false,"percent_used":33.3}],\
    "at_limit":false,"cooldown_until":null,"exhausted":[]}}
    """
    static let usageBody = #"{"five_hour":{"utilization":12.5,"resets_at":"2026-09-23T20:00:00Z"}}"#
    /// A made-up sign-in value; it is not a real token and never leaves the fixture.
    static let fixtureToken = "smoke-fixture-not-a-real-token"
    /// Every comparison in the run reads this instant, never the wall clock.
    public static let fixtureNow = AccountUsageService.date("2026-09-23T10:00:00Z")!

    /// The injected service: fixture clock, fake transport, fixture credential.
    public static func fixture() -> (service: AccountUsageService, transport: AccountResetSmokeTransport) {
        let transport = AccountResetSmokeTransport()
        let service = AccountUsageService(now: { fixtureNow }, probe: { _ in
            try await AccountUsageService.claude(environment: [:],
                load: { ClaudeQuotaCredential(token: fixtureToken, plan: "max") },
                http: { try await transport.respond($0) },
                now: { fixtureNow })
        })
        return (service, transport)
    }

    /// The expected GET allow-list for the run, in order: the base usage read,
    /// the profile read and the two entitlement variants, each carrying skip_spend=1.
    public static let expectedRequests = [
        "GET /api/oauth/usage",
        "GET /api/oauth/profile",
        "GET /api/oauth/usage?cedar_ember=1&skip_spend=1",
        "GET /api/oauth/usage?at_wall=1&skip_spend=1",
    ]

    /// Scores a finished run. `directLookupEnabled` is true because the smoke's
    /// service is injected: the rows render without the switch and without
    /// touching it.
    public static func score(_ snapshot: AccountUsageSnapshot?, transport: AccountResetSmokeTransport) -> AccountResetSmokeResult {
        let rows = AccountResetPresentation.rows(snapshot, directLookupEnabled: true)
        let cedar = rows.first { $0.program == ResetProgram.cedarEmber.rawValue }
        let juniper = rows.first { $0.program == ResetProgram.juniperTide.rawValue }
        let lines = rows.map(\.line)
        let cedarState = cedar?.state ?? ResetState.unknown.rawValue
        let juniperState = juniper?.state ?? ResetState.unknown.rawValue
        let resolved = !lines.isEmpty && lines.allSatisfy { !$0.isEmpty && !$0.hasPrefix("usage.reset.") }
        return AccountResetSmokeResult(
            passed: transport.postCount == 0 && cedarState == "available" && juniperState == "unknown"
                && transport.requests == expectedRequests && resolved,
            fakeTransportPostCount: transport.postCount,
            cedarEmberState: cedarState, juniperTideState: juniperState,
            requests: transport.requests, lines: lines)
    }

    /// The whole run, for the test and for anything that needs no controller.
    public static func run() async -> AccountResetSmokeResult {
        let (service, transport) = fixture()
        let snapshot = await service.read(provider: "claude", force: true)
        await service.shutdown()
        return score(snapshot, transport: transport)
    }
}
