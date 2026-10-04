import Foundation
import Network

/// What the control socket cares about in the Mac's network: whether there is
/// one at all, and which interfaces and routers carry it. A Wi-Fi switch, a VPN
/// coming up or a cable pulled changes one of these; a mere "expensive" or
/// "constrained" flip does not, and must not cost the phones their tunnels.
struct RelayNetworkPath: Equatable, Sendable {
    var satisfied: Bool
    var interfaces: [String]
    var gateways: [String]

    init(satisfied: Bool, interfaces: [String], gateways: [String] = []) {
        self.satisfied = satisfied; self.interfaces = interfaces; self.gateways = gateways
    }
    /// `.requiresConnection` counts as a network: dialling is what brings an
    /// on-demand VPN up, so treating it as offline would never dial at all.
    init(status: NWPath.Status, interfaces: [String], gateways: [String] = []) {
        self.init(satisfied: status != .unsatisfied, interfaces: interfaces, gateways: gateways)
    }
    init(_ path: NWPath) {
        self.init(status: path.status, interfaces: path.availableInterfaces.map(\.name).sorted(),
                  gateways: path.gateways.map(\.debugDescription).sorted())
    }
}

/// When the host drops its control socket and dials the relay again. The
/// relay forgets a host whose socket stops answering its pings, but the Mac's
/// side of a connection the network pulled out from under it never errors on
/// its own, so these decisions are made here rather than left to `receive()`.
enum RelayLinkPolicy {
    /// How often a connected control socket is pinged. With the 10 s ping
    /// deadline, a dead socket is noticed at worst 20 + 10 = 30 s after it died.
    /// The relay's own pings are WebSocket control frames that URLSession
    /// answers by itself; they never reach the app, so they cannot tell it.
    static let keepaliveInterval: TimeInterval = 20
    /// How long path reports must stay quiet before one decision is made: a
    /// Wi-Fi switch reports several paths within a second, and each must not
    /// redial. A path change therefore reconnects about 1.5 s after it settles.
    static let pathSettle: TimeInterval = 1.5
    /// A stream of reports that never goes quiet still settles after this long.
    static let pathSettleMaxWait: TimeInterval = 5
    /// Path-triggered redials are at least this far apart, the gap doubling
    /// while they keep coming (a flapping link) up to `restartGapMax`.
    static let restartGapMin: TimeInterval = 5
    static let restartGapMax: TimeInterval = 30

    enum PathAction: Equatable { case none, offline, reconnect }

    /// What the path settle and the redial pacing read the time from and wait
    /// on. The real clock in the app; tests move a manual one by hand, so a
    /// settle never races a test's own sleep.
    struct Clock: Sendable {
        var now: @Sendable () -> Date
        /// Returns at `deadline` (at once when it has passed).
        var sleep: @Sendable (_ until: Date) async throws -> Void
        static let system = Clock(now: { Date() }, sleep: { deadline in
            try await Task.sleep(for: .seconds(max(0, deadline.timeIntervalSinceNow)))
        })
    }

    /// Everything reported since the last decision, not only the last report:
    /// a Wi-Fi switch between two routers on the same 192.168.x.1 reports
    /// wifi → nothing → wifi, and the settled path then equals the old one
    /// although the socket under it is gone.
    struct Window: Equatable {
        /// The latest report, or nil when only a wake opened the window.
        var latest: RelayNetworkPath?
        /// Some report differed from the settled path or had no network.
        var disturbed = false
        /// A wake asked for a redial whatever the path does.
        var forced = false
        /// When the window opened, for `pathSettleMaxWait`.
        var openedAt: Date?

        var isEmpty: Bool { latest == nil && !forced }

        mutating func report(_ path: RelayNetworkPath, settled: RelayNetworkPath?, now: Date) {
            if openedAt == nil { openedAt = now }
            if let settled, path != settled || !path.satisfied { disturbed = true }
            latest = path
        }
        mutating func force(now: Date) {
            if openedAt == nil { openedAt = now }
            forced = true
        }
        /// How long to wait before deciding: the settle delay, cut short so the
        /// window never stays open longer than `pathSettleMaxWait`.
        func delay(now: Date) -> TimeInterval {
            guard let openedAt else { return RelayLinkPolicy.pathSettle }
            return max(0, min(RelayLinkPolicy.pathSettle, openedAt.addingTimeInterval(RelayLinkPolicy.pathSettleMaxWait).timeIntervalSince(now)))
        }
    }

    /// The minimum gap between path-triggered redials. A redial more than the
    /// current gap plus `restartGapMax` after the last one starts over at
    /// `restartGapMin`; one sooner doubles the gap.
    struct Pacing: Equatable {
        private(set) var last: Date?
        private(set) var streak = 0

        var gap: TimeInterval { min(RelayLinkPolicy.restartGapMax, RelayLinkPolicy.restartGapMin * pow(2, Double(streak))) }
        /// How long a redial wanted `now` has to wait.
        func wait(now: Date) -> TimeInterval {
            guard let last else { return 0 }
            return max(0, last.addingTimeInterval(gap).timeIntervalSince(now))
        }
        mutating func record(now: Date) {
            if let last, now.timeIntervalSince(last) < gap + RelayLinkPolicy.restartGapMax { streak += 1 } else { streak = 0 }
            last = now
        }
    }

    /// `old` is the path the current connection was made on (nil before the
    /// first report), `new` the settled one (nil when only `forced` fired).
    /// No network reads as offline rather than as a reason to redial; any other
    /// difference, or a `disturbed` window, means the old socket may be
    /// half-open, so it is replaced.
    static func action(from old: RelayNetworkPath?, to new: RelayNetworkPath?, forced: Bool, disturbed: Bool = false) -> PathAction {
        guard let new else { return forced ? .reconnect : .none }
        guard new.satisfied else { return old?.satisfied == false && !forced ? .none : .offline }
        if forced || disturbed { return .reconnect }
        guard let old else { return .none }
        return old == new ? .none : .reconnect
    }
    static func action(from old: RelayNetworkPath?, window: Window) -> PathAction {
        action(from: old, to: window.latest, forced: window.forced, disturbed: window.disturbed)
    }
}
