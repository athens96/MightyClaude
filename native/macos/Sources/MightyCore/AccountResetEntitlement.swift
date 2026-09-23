import Foundation

/// The Claude limit-reset (리셋권) entitlement, read only.
///
/// Two programs are reported through the same GET-only OAuth usage surface the
/// installed Claude Code CLI asks (2.1.280):
///   cedar_ember  — granted resets, a count plus an expiry.
///   juniper_tide — the at-wall reset, a next time plus a weekly count.
/// The app never claims, spends or writes one; the only action is the link to
/// claude.ai Settings > Usage, and that link is live in every state.
public enum ResetProgram: String, Codable, Sendable, CaseIterable {
    case cedarEmber = "cedar_ember"
    case juniperTide = "juniper_tide"

    /// The top-level key the CLI reads the block from in the usage response.
    var bodyKey: String { rawValue }
    var labelKey: String { self == .cedarEmber ? "usage.reset.program.cedarEmber" : "usage.reset.program.juniperTide" }
    /// The one query variant that carries this program, `skip_spend=1` included.
    public var query: String { self == .cedarEmber ? "cedar_ember=1&skip_spend=1" : "at_wall=1&skip_spend=1" }
}

/// Seven states. Precedence, highest first:
/// available > held > cooldown > exhausted > none > ineligible > unknown.
public enum ResetState: String, Codable, Sendable, CaseIterable {
    case available, held, cooldown, exhausted, none, ineligible, unknown

    /// Higher wins when two readings of the same program disagree.
    public var precedence: Int { ResetState.allCases.firstIndex(of: self).map { ResetState.allCases.count - $0 } ?? 0 }
}

/// One rendered 리셋권 row. Carries no credential, no grant id and no account
/// identifier — only what the row shows.
public struct AccountResetEntitlement: Codable, Equatable, Sendable, Identifiable {
    public var program: String
    public var state: String
    /// cedar_ember: the selected grant's resets_left, shown as "남은 리셋권 N회".
    public var remainingCount: Int?
    /// cedar_ember: the selected grant's ends_at, shown as "{만료일}까지".
    public var expiresAt: String?
    /// juniper_tide: next_available_at, and cedar_ember: cooldown_until.
    public var nextAvailableAt: String?
    /// juniper_tide: weekly_resets_at, carried beside the next time.
    public var weeklyResetsAt: String?
    /// juniper_tide: resets_per_week, shown as "주 N회".
    public var resetsPerWeek: Int?
    public var id: String { program }

    public init(program: ResetProgram, state: ResetState, remainingCount: Int? = nil, expiresAt: String? = nil,
                nextAvailableAt: String? = nil, weeklyResetsAt: String? = nil, resetsPerWeek: Int? = nil) {
        self.program = program.rawValue; self.state = state.rawValue
        self.remainingCount = remainingCount; self.expiresAt = expiresAt
        self.nextAvailableAt = nextAvailableAt; self.weeklyResetsAt = weeklyResetsAt; self.resetsPerWeek = resetsPerWeek
    }

    public var kind: ResetProgram { ResetProgram(rawValue: program) ?? .cedarEmber }
    public var status: ResetState { ResetState(rawValue: state) ?? .unknown }
    /// The program's own name; the same key on macOS and Windows.
    public var label: String { L(kind.labelKey) }

    /// Exactly one string per program and state. The available state renders
    /// the per-program line; every other state renders the per-state sentence.
    /// `unknown` blames this app's connection, never Anthropic's policy.
    public var copyKey: String {
        switch status {
        case .available: return kind == .cedarEmber ? "usage.reset.available.cedarEmber" : "usage.reset.available.juniperTide"
        case .cooldown: return kind == .cedarEmber ? "usage.reset.cooldown.cedarEmber" : "usage.reset.cooldown.juniperTide"
        case .held: return "usage.reset.held"
        case .exhausted: return "usage.reset.exhausted"
        case .none: return "usage.reset.none"
        case .ineligible: return "usage.reset.ineligible"
        case .unknown: return "usage.reset.unknown"
        }
    }

    public var line: String {
        switch (status, kind) {
        case (.available, .cedarEmber):
            return L(copyKey, ["count": String(remainingCount ?? 0), "expiry": Self.day(expiresAt)])
        case (.cooldown, .cedarEmber):
            return L(copyKey, ["time": Self.moment(nextAvailableAt)])
        case (.cooldown, .juniperTide):
            return L(copyKey, ["time": Self.moment(nextAvailableAt), "count": String(resetsPerWeek ?? 0)])
        default:
            return L(copyKey)
        }
    }

    /// The link is enabled in every state; it opens claude.ai Settings > Usage
    /// in the browser. The app itself never resets.
    public static let linkKey = "usage.reset.link"
    public static let linkTarget = "https://claude.ai/settings/usage"
    public static var linkLabel: String { L(linkKey) }

    static func day(_ value: String?) -> String {
        guard let value, let date = AccountUsageService.date(value) else { return "" }
        return date.formatted(date: .abbreviated, time: .omitted)
    }
    static func moment(_ value: String?) -> String {
        guard let value, let date = AccountUsageService.date(value) else { return "" }
        return date.formatted(date: .omitted, time: .shortened)
    }
}

/// The decision lists, evaluated top to bottom, first match wins. Every time
/// comparison reads the caller's `now`, never the wall clock.
public enum ClaudeResetEntitlements {
    /// Every read of a block that could not be understood ends here.
    public static func unknown(_ program: ResetProgram) -> AccountResetEntitlement {
        AccountResetEntitlement(program: program, state: .unknown)
    }
    /// A 404, or a 200 whose body simply has no reset fields, means the
    /// account is not in the programme — not that this app cannot see it.
    public static func ineligible(_ program: ResetProgram) -> AccountResetEntitlement {
        AccountResetEntitlement(program: program, state: .ineligible)
    }

    private static func flag(_ value: Any?) -> Bool? {
        guard let number = value as? NSNumber, CFGetTypeID(number) == CFBooleanGetTypeID() else { return nil }
        return number.boolValue
    }
    private static func whole(_ value: Any?) -> Int? {
        guard let value = AccountUsageService.number(value), value >= 0, value <= 1_000_000, value.rounded() == value else { return nil }
        return Int(value)
    }
    private static func instant(_ value: Any?) -> Date? {
        guard let raw = value as? String else { return nil }
        return AccountUsageService.date(raw)
    }
    /// `ineligible_reason` marks the state; its value is never matched literally
    /// and never shown, so a new server reason still reads as ineligible.
    private static func reasonPresent(_ block: [String: Any]) -> Bool {
        guard let value = block["ineligible_reason"] else { return false }
        if value is NSNull { return false }
        if let text = value as? String { return !text.isEmpty }
        return true
    }

    /// cedar_ember: granted resets. Field names are the CLI 2.1.280 decoder's.
    public static func cedarEmber(_ body: Any?, now: Date) -> AccountResetEntitlement {
        // (1) a parse or HTTP failure, a non-JSON body, a wrong-typed field or
        //     a missing block — this app cannot tell, so it says so.
        guard let root = body as? [String: Any], let block = root["cedar_ember"] as? [String: Any] else { return unknown(.cedarEmber) }
        if let raw = block["eligible"], flag(raw) == nil { return unknown(.cedarEmber) }
        if let raw = block["grants"], !(raw is [Any]) { return unknown(.cedarEmber) }
        if let raw = block["next_grant_id"], !(raw is String), !(raw is NSNull) { return unknown(.cedarEmber) }
        // (2) the server says this account is not in the programme.
        //     cedar_ember reports `eligible`; `in_experiment` is accepted too
        //     because juniper_tide spells the same fact that way.
        if flag(block["eligible"]) == false || flag(block["in_experiment"]) == false || reasonPresent(block) { return ineligible(.cedarEmber) }
        // (3) no grant is selected, or the selected id matches nothing.
        guard let selected = block["next_grant_id"] as? String, !selected.isEmpty,
              let grants = block["grants"] as? [Any],
              let grant = grants.compactMap({ $0 as? [String: Any] }).first(where: { $0["id"] as? String == selected })
        else { return AccountResetEntitlement(program: .cedarEmber, state: .none) }

        let left = whole(grant["resets_left"])
        let ends = instant(grant["ends_at"])
        let expiry = ends.map(AccountUsageService.timestamp)
        // (4) a paused grant, or one whose window already closed.
        if flag(grant["paused"]) == true { return AccountResetEntitlement(program: .cedarEmber, state: .none) }
        if let ends, ends <= now { return AccountResetEntitlement(program: .cedarEmber, state: .none) }
        // (5) usable right now.
        if flag(grant["usable_now"]) == true {
            return AccountResetEntitlement(program: .cedarEmber, state: .available, remainingCount: left, expiresAt: expiry)
        }
        // (6) held back until the account actually reaches its limit.
        if flag(grant["use_requires_limit"]) == true && flag(block["at_limit"]) != true {
            return AccountResetEntitlement(program: .cedarEmber, state: .held, remainingCount: left, expiresAt: expiry)
        }
        // (7) still cooling down from the last use.
        if let cooldown = instant(block["cooldown_until"]), cooldown > now {
            return AccountResetEntitlement(program: .cedarEmber, state: .cooldown, remainingCount: left,
                                           expiresAt: expiry, nextAvailableAt: AccountUsageService.timestamp(cooldown))
        }
        // (8) nothing left in this period — `exhausted` is a list or a flag.
        let exhausted = (block["exhausted"] as? [Any])?.isEmpty == false || flag(block["exhausted"]) == true
        if exhausted || left == 0 {
            return AccountResetEntitlement(program: .cedarEmber, state: .exhausted, remainingCount: left, expiresAt: expiry)
        }
        // (9) otherwise there is nothing to offer.
        return AccountResetEntitlement(program: .cedarEmber, state: .none, remainingCount: left, expiresAt: expiry)
    }

    /// juniper_tide: the reset offered when the account is at the wall.
    public static func juniperTide(_ body: Any?, now: Date) -> AccountResetEntitlement {
        // (1) a parse or HTTP failure, or a missing block.
        guard let root = body as? [String: Any], let block = root["juniper_tide"] as? [String: Any] else { return unknown(.juniperTide) }
        if let raw = block["in_experiment"], flag(raw) == nil { return unknown(.juniperTide) }
        if let raw = block["available"], flag(raw) == nil { return unknown(.juniperTide) }
        // (2) not in the experiment, or the server named a reason.
        if flag(block["in_experiment"]) == false || reasonPresent(block) { return ineligible(.juniperTide) }
        let perWeek = whole(block["resets_per_week"])
        let weekly = instant(block["weekly_resets_at"]).map(AccountUsageService.timestamp)
        // (3) available right now.
        if flag(block["available"]) == true {
            return AccountResetEntitlement(program: .juniperTide, state: .available, weeklyResetsAt: weekly, resetsPerWeek: perWeek)
        }
        // (4) a next time still ahead of the injected clock.
        if let next = instant(block["next_available_at"]), next > now {
            return AccountResetEntitlement(program: .juniperTide, state: .cooldown,
                                           nextAvailableAt: AccountUsageService.timestamp(next),
                                           weeklyResetsAt: weekly, resetsPerWeek: perWeek)
        }
        // (5) the account gets none this week.
        if perWeek == 0 { return AccountResetEntitlement(program: .juniperTide, state: .none, weeklyResetsAt: weekly, resetsPerWeek: perWeek) }
        // (6) otherwise this period's resets are spent.
        return AccountResetEntitlement(program: .juniperTide, state: .exhausted, weeklyResetsAt: weekly, resetsPerWeek: perWeek)
    }
}

/// The rows the UI draws. The decision to hide them lives here in Core, not in
/// a view controller, so both platforms and their tests share one rule.
public enum AccountResetPresentation {
    /// The 리셋권 rows are hidden entirely while the direct-lookup switch is
    /// off — nothing is teased and nothing stale is shown. With the switch on
    /// and no read yet, every programme reads `unknown`.
    public static func rows(_ snapshot: AccountUsageSnapshot?, directLookupEnabled: Bool) -> [AccountResetEntitlement] {
        guard directLookupEnabled else { return [] }
        let read = snapshot?.resets ?? []
        return ResetProgram.allCases.map { program in
            read.first { $0.program == program.rawValue } ?? ClaudeResetEntitlements.unknown(program)
        }
    }
}
