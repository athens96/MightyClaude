import Foundation

/// When the background CLI and plugin updates run while an automatic update
/// setting is on: a pass at launch, then one every `interval`. A provider is
/// only updated once it has been idle for `idleDelay` (nothing running,
/// starting or queued), so a background update never gets in the way of work
/// the user just did; one that was not idle is retried once it is. Updates
/// apply from the next request, since every request starts a new CLI process.
public struct CLIAutoUpdateSchedule: Sendable, Equatable {
    public static let interval: TimeInterval = 6 * 60 * 60
    /// How often the app looks whether a pass or a retry is due.
    public static let tick: TimeInterval = 60
    public static let idleDelay: TimeInterval = 3 * 60
    /// No new plugin update starts once a pass has spent this long on plugins.
    public static let pluginBudget: TimeInterval = 5 * 60
    public private(set) var lastPass: Date?
    /// Providers skipped because they were not idle, waiting to be.
    public private(set) var deferred: Set<String> = []

    public init() {}

    public func isDue(at now: Date, enabled: Bool) -> Bool {
        guard enabled else { return false }
        guard let lastPass else { return true }
        return now.timeIntervalSince(lastPass) >= Self.interval
    }
    public mutating func passStarted(at now: Date) { lastPass = now }
    public mutating func skippedBusy(_ provider: String) { deferred.insert(provider) }
    public mutating func updated(_ provider: String) { deferred.remove(provider) }

    /// A provider may update in the background: nothing of it runs, starts or
    /// waits in a queue, and its last activity is `idleDelay` old (or none yet).
    public static func idleLongEnough(busy: Bool, queued: Bool, lastActive: Date?, now: Date) -> Bool {
        guard !busy, !queued else { return false }
        guard let lastActive else { return true }
        return now.timeIntervalSince(lastActive) >= idleDelay
    }

    /// Deferred providers that are ready now, taken out of the set. Turning
    /// the settings off forgets every deferred provider.
    public mutating func dueRetries(enabled: Bool, ready: (String) -> Bool) -> [String] {
        guard enabled else { deferred.removeAll(); return [] }
        let due = deferred.filter(ready).sorted()
        deferred.subtract(due)
        return due
    }

    /// A provider's plugins change only while it is idle and the plugin browser
    /// is not changing plugins itself.
    public static func mayUpdatePlugins(busy: Bool, managingPlugins: Bool) -> Bool { !busy && !managingPlugins }
}

/// The background plugin update of one provider.
public struct PluginAutoUpdateResult: Sendable, Equatable {
    /// succeeded, skipped, failed, busy, or cancelled.
    public var status: String
    public var detail: String
    public var updated: [String]
    /// Plugins whose update asks to approve a changed marketplace command.
    /// The app never approves it; the plugin browser is where the user can.
    public var needsApproval: [String]
    public var failed: [String]
    public init(status: String, detail: String, updated: [String] = [], needsApproval: [String] = [], failed: [String] = []) {
        self.status = status; self.detail = detail; self.updated = updated; self.needsApproval = needsApproval; self.failed = failed
    }
}
