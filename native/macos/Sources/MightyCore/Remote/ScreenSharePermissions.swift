import Foundation

/// The macOS permissions screen sharing leans on, in the order the onboarding
/// screen lists them. None of them is a safety rule — the host enforces the
/// allow-list and grants itself — but without them a session shows nothing,
/// injects nothing, takes the long way round, or starts without telling anyone.
public enum ScreenSharePermission: String, CaseIterable, Sendable {
    /// `CGPreflightScreenCaptureAccess`: ScreenCaptureKit delivers no frames without it.
    case screenRecording
    /// `CGPreflightPostEventAccess` / `AXIsProcessTrusted`: `CGEventPost` is dropped without it.
    case accessibility
    /// Same-Wi-Fi ICE host candidates; without it the video goes through TURN.
    case localNetwork
    /// The notice the Mac shows when a session starts.
    case notifications

    /// The System Settings pane that grants it.
    public func settingsURL(bundleIdentifier: String? = nil) -> URL {
        let raw: String
        switch self {
        case .screenRecording: raw = "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture"
        case .accessibility: raw = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"
        case .localNetwork: raw = "x-apple.systempreferences:com.apple.preference.security?Privacy_LocalNetwork"
        case .notifications:
            // The notifications pane opens on the app's own row when it is named.
            let id = bundleIdentifier.flatMap { $0.isEmpty ? nil : $0 }
            raw = "x-apple.systempreferences:com.apple.Notifications-Settings.extension" + (id.map { "?id=" + $0 } ?? "")
        }
        return URL(string: raw)!
    }
}

/// What the onboarding screen says about one permission.
public enum ScreenSharePermissionStatus: String, Sendable, Equatable {
    case granted
    /// Refused, or never turned on in its System Settings list.
    case missing
    /// macOS has not asked yet.
    case notDetermined
    /// macOS offers no way to read it (local network on some systems).
    case unknown

    /// The states worth sending the user to System Settings for. `unknown` is
    /// not one: nagging about a permission the app cannot read helps nobody.
    public var needsAttention: Bool { self == .missing || self == .notDetermined }
}

/// The raw readings turned into statuses. Kept apart from the system calls so
/// the mapping is tested without TCC.
public enum ScreenSharePermissionReading {
    public static func screenRecording(preflight: Bool) -> ScreenSharePermissionStatus {
        preflight ? .granted : .missing
    }

    /// Posting events needs the app in the Accessibility list; either reading
    /// saying yes is enough, since the post-event check is the narrower one.
    public static func accessibility(postEventAccess: Bool, processTrusted: Bool) -> ScreenSharePermissionStatus {
        postEventAccess || processTrusted ? .granted : .missing
    }

    /// `UNAuthorizationStatus.rawValue`: 0 not determined, 1 denied,
    /// 2 authorized, 3 provisional, 4 ephemeral.
    public static func notifications(authorizationStatus raw: Int) -> ScreenSharePermissionStatus {
        switch raw {
        case 0: return .notDetermined
        case 1: return .missing
        case 2, 3, 4: return .granted
        default: return .unknown
        }
    }

    /// What a short Bonjour browse reported. macOS has no API that reads the
    /// local-network grant, so the app tries a local operation and reads how it
    /// failed (TN3179).
    public enum LocalNetworkSignal: Sendable, Equatable {
        /// The browser reached `.ready` and no refusal followed.
        case ready
        /// `kDNSServiceErr_PolicyDenied`: the user said no, or has not answered.
        case policyDenied
        /// Some other error, or no answer before the probe gave up.
        case inconclusive
    }

    public static func localNetwork(_ signal: LocalNetworkSignal) -> ScreenSharePermissionStatus {
        switch signal {
        case .ready: return .granted
        case .policyDenied: return .missing
        case .inconclusive: return .unknown
        }
    }

    /// Whether allowing a phone should bring the onboarding screen up.
    public static func needsOnboarding(_ statuses: [ScreenSharePermission: ScreenSharePermissionStatus]) -> Bool {
        statuses.values.contains { $0.needsAttention }
    }
}
