import Foundation

/// The app's steps of the automatic update that are decisions rather than
/// work, beside the rules in `AppUpdateAuto.swift`: what a finished check does
/// with an update already staged, and whether quitting installs it.
extension AppUpdateAuto {
    /// What the app does after a check succeeded.
    public enum CheckOutcome: Sendable, Equatable {
        /// The staged update is the version the manifest names, or the newer
        /// version the manifest names cannot run on this Mac: either way it
        /// stays ready and nothing is downloaded again.
        case keepStaged
        /// A newer version exists but cannot run on this Mac (the reason is shown).
        case blocked(String)
        /// Nothing newer for this Mac.
        case upToDate
        /// A newer version for this Mac; `download` starts it without asking
        /// (the "automatic" switch is on).
        case available(download: Bool)
    }

    /// - Parameters:
    ///   - availability: what the check found.
    ///   - stagedVersion: the version staged and ready to install, if any.
    ///   - automatic: the "automatic" switch.
    public static func afterCheck(_ availability: AppUpdateAvailability, stagedVersion: String?, automatic: Bool) -> CheckOutcome {
        if let stagedVersion, AppVersion.normalized(stagedVersion) != nil, AppVersion.normalized(availability.manifest.version) != nil,
           AppVersion.compare(stagedVersion, availability.manifest.version) == .orderedSame { return .keepStaged }
        if let reason = availability.blockedReason { return stagedVersion == nil ? .blocked(reason) : .keepStaged }
        guard availability.isNewer, availability.manifest.macos != nil else { return .upToDate }
        return .available(download: automatic)
    }

    /// Whether quitting the app installs the staged update (the helper runs
    /// with `relaunch: false`).
    /// - Parameters:
    ///   - ready: an update is staged and no install is under way (an install
    ///     already started, by "restart now" or the countdown, quits on its own).
    ///   - automatic: the "automatic" switch; turned off, only "restart now" installs.
    ///   - hasKey: the build verifies updates and this is not a smoke test or the
    ///     help capture.
    ///   - poweringOff: the Mac is logging out, restarting or shutting down
    ///     (`quitReasonPowersOff`); the swap could be cut off half-way, so the
    ///     update waits for the next quit.
    public static func installsOnQuit(ready: Bool, automatic: Bool, hasKey: Bool, poweringOff: Bool) -> Bool {
        ready && automatic && hasKey && !poweringOff
    }

    /// The `kAEQuitReason` codes of a quit sent by a logout, restart or
    /// shutdown (`kAELogOut`, `kAEReallyLogOut`, `kAEShowRestartDialog`,
    /// `kAEShowShutdownDialog`, `kAERestart`, `kAEShutDown`), as four-char codes.
    public static let powerOffQuitReasons: Set<UInt32> = Set(["logo", "rlgo", "rrst", "rsdn", "rest", "shut"].map(fourCharCode))

    /// Whether a quit whose Apple event carries this `kAEQuitReason` enum code
    /// comes from a logout, restart or shutdown. Decided at quit time, so a
    /// logout the user cancelled leaves nothing behind; a quit with no reason,
    /// or any other reason, is an ordinary quit.
    public static func quitReasonPowersOff(_ code: UInt32?) -> Bool {
        code.map(powerOffQuitReasons.contains) ?? false
    }

    private static func fourCharCode(_ text: String) -> UInt32 { text.utf8.reduce(0) { $0 << 8 | UInt32($1) } }

    /// The folders under the app's `updates/` (one per downloaded version,
    /// named by `AppVersion.normalized`) to delete at launch: every version not
    /// newer than the one running. Such a copy was installed already (its
    /// helper was cut off after the swap) or is older, and is never offered.
    /// Names that are not versions are left alone.
    public static func staleStagedFolders(_ names: [String], runningVersion: String) -> [String] {
        names.filter { AppVersion.normalized($0) != nil && !AppVersion.isNewer($0, than: runningVersion) }
    }
}
