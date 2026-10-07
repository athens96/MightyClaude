import AppKit
import ApplicationServices
import CoreGraphics
import dnssd
import MightyCore
import Network
import SwiftUI
import UserNotifications

/// Reads the four permissions screen sharing leans on. Each reading is a
/// system call; what it means is `ScreenSharePermissionReading`'s business.
enum ScreenSharePermissionReader {
    /// Screen Recording, Accessibility and Notifications: cheap enough to poll.
    @MainActor
    static func readQuick() async -> [ScreenSharePermission: ScreenSharePermissionStatus] {
        let notifications = await UNUserNotificationCenter.current().notificationSettings().authorizationStatus
        return [
            .screenRecording: ScreenSharePermissionReading.screenRecording(preflight: CGPreflightScreenCaptureAccess()),
            .accessibility: ScreenSharePermissionReading.accessibility(
                postEventAccess: CGPreflightPostEventAccess(), processTrusted: AXIsProcessTrusted()),
            .notifications: ScreenSharePermissionReading.notifications(authorizationStatus: notifications.rawValue),
        ]
    }

    /// A short Bonjour browse. A refusal arrives as `kDNSServiceErr_PolicyDenied`;
    /// a browser that reached `.ready` and stayed there is taken as allowed.
    /// The first probe is also what makes macOS ask, when it has not yet.
    static func readLocalNetwork(timeout: TimeInterval = 1.5) async -> ScreenSharePermissionStatus {
        let probe = LocalNetworkProbe()
        return ScreenSharePermissionReading.localNetwork(await probe.run(timeout: timeout))
    }
}

/// One Bonjour browse, resolved exactly once.
private final class LocalNetworkProbe: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<ScreenSharePermissionReading.LocalNetworkSignal, Never>?
    private var reachedReady = false
    private var browser: NWBrowser?

    func run(timeout: TimeInterval) async -> ScreenSharePermissionReading.LocalNetworkSignal {
        await withCheckedContinuation { continuation in
            lock.lock(); self.continuation = continuation; lock.unlock()
            let browser = NWBrowser(for: .bonjour(type: "_mightyclaude._tcp", domain: "local."), using: .tcp)
            browser.stateUpdateHandler = { [weak self] state in
                guard let self else { return }
                switch state {
                case .ready:
                    lock.lock(); reachedReady = true; lock.unlock()
                case .waiting(let error), .failed(let error):
                    if case .dns(let code) = error, code == DNSServiceErrorType(kDNSServiceErr_PolicyDenied) {
                        finish(.policyDenied)
                    } else {
                        finish(.inconclusive)
                    }
                default:
                    break
                }
            }
            lock.lock(); self.browser = browser; lock.unlock()
            browser.start(queue: .global(qos: .utility))
            DispatchQueue.global().asyncAfter(deadline: .now() + timeout) { [weak self] in
                guard let self else { return }
                lock.lock(); let ready = reachedReady; lock.unlock()
                finish(ready ? .ready : .inconclusive)
            }
        }
    }

    private func finish(_ signal: ScreenSharePermissionReading.LocalNetworkSignal) {
        lock.lock()
        let pending = continuation
        continuation = nil
        let browser = self.browser
        self.browser = nil
        lock.unlock()
        browser?.cancel()
        pending?.resume(returning: signal)
    }
}

/// The permission onboarding sheet: what each permission is for, whether it is
/// on, and a button to the System Settings pane that turns it on.
struct ScreenSharePermissionsView: View {
    @Environment(\.dismiss) private var dismiss
    @ViewState private var statuses: [ScreenSharePermission: ScreenSharePermissionStatus] = [:]
    @ViewState private var checking = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: "lock.shield").foregroundStyle(.secondary)
                Text(L("settings.screenShare.permissions.title")).font(.system(size: 14, weight: .semibold))
                BetaBadge()
                Spacer()
            }
            .padding(DesignMetrics.Inset.sheet)
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.lg) {
                    Text(L("settings.screenShare.permissions.intro"))
                        .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    ForEach(ScreenSharePermission.allCases, id: \.self) { permission in
                        row(permission)
                    }
                    Label(L("settings.screenShare.permissions.signingNote"), systemImage: "info.circle")
                        .font(.system(size: 11)).foregroundStyle(Palette.waitText).fixedSize(horizontal: false, vertical: true)
                        .accessibilityIdentifier("screen-share-permissions-signing-note")
                }
                .padding(DesignMetrics.Inset.sheet)
            }
            Divider()
            HStack {
                Button(L("settings.screenShare.permissions.recheck")) { Task { await refresh(includeLocalNetwork: true) } }
                    .disabled(checking)
                Spacer()
                Button(L("settings.screenShare.permissions.close")) { dismiss() }.keyboardShortcut(.cancelAction)
            }
            .padding(DesignMetrics.Inset.sheet)
        }
        .frame(width: 520, height: 560)
        .accessibilityIdentifier("screen-share-permissions")
        .task {
            await refresh(includeLocalNetwork: true)
            // The user flips switches in System Settings while this is open.
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(2))
                await refresh(includeLocalNetwork: false)
            }
        }
    }

    private func row(_ permission: ScreenSharePermission) -> some View {
        let status = statuses[permission] ?? .unknown
        return VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: Self.symbol(permission)).frame(width: 18).foregroundStyle(.secondary)
                Text(Self.title(permission)).font(.system(size: 12, weight: .medium))
                statusChip(status)
                Spacer(minLength: 8)
                Group {
                    if permission == .notifications, status == .notDetermined {
                        // An app macOS never asked about has no row in the pane yet.
                        Button(L("settings.screenShare.permissions.requestButton")) {
                            Task {
                                _ = try? await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound])
                                await refresh(includeLocalNetwork: false)
                            }
                        }
                        .controlSize(.small)
                    } else {
                        Button(L("settings.screenShare.permissions.openSettings")) {
                            NSWorkspace.shared.open(permission.settingsURL(bundleIdentifier: Bundle.main.bundleIdentifier))
                        }
                        .controlSize(.small)
                    }
                }
                .accessibilityIdentifier("screen-share-permission-open-\(permission.rawValue)")
            }
            Text(Self.explanation(permission))
                .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                .padding(.leading, 18 + DesignMetrics.Spacing.sm) // under the title: the icon column and its gap
        }
        .accessibilityIdentifier("screen-share-permission-\(permission.rawValue)")
    }

    private func statusChip(_ status: ScreenSharePermissionStatus) -> some View {
        let (text, foreground, background): (String, Color, Color) = {
            switch status {
            case .granted: return (L("settings.screenShare.permissions.statusGranted"), Palette.doneText, Palette.doneSoft)
            case .missing: return (L("settings.screenShare.permissions.statusMissing"), Palette.stopText, Palette.stopSoft)
            case .notDetermined: return (L("settings.screenShare.permissions.statusNotDetermined"), Palette.waitText, Palette.waitSoft)
            case .unknown: return (L("settings.screenShare.permissions.statusUnknown"), Color.secondary, Color.secondary.opacity(0.12))
            }
        }()
        return Text(text).font(.system(size: 9, weight: .medium)).foregroundStyle(foreground)
            .padding(.horizontal, DesignMetrics.Spacing.xs).padding(.vertical, 1)
            .background(background, in: Capsule())
    }

    private func refresh(includeLocalNetwork: Bool) async {
        checking = true
        defer { checking = false }
        var next = await ScreenSharePermissionReader.readQuick()
        if includeLocalNetwork {
            next[.localNetwork] = await ScreenSharePermissionReader.readLocalNetwork()
        } else {
            next[.localNetwork] = statuses[.localNetwork] ?? .unknown
        }
        statuses = next
    }

    private static func symbol(_ permission: ScreenSharePermission) -> String {
        switch permission {
        case .screenRecording: return "rectangle.dashed.badge.record"
        case .accessibility: return "hand.tap"
        case .localNetwork: return "wifi"
        case .notifications: return "bell"
        }
    }

    private static func title(_ permission: ScreenSharePermission) -> String {
        switch permission {
        case .screenRecording: return L("settings.screenShare.permissions.screenRecordingTitle")
        case .accessibility: return L("settings.screenShare.permissions.accessibilityTitle")
        case .localNetwork: return L("settings.screenShare.permissions.localNetworkTitle")
        case .notifications: return L("settings.screenShare.permissions.notificationsTitle")
        }
    }

    private static func explanation(_ permission: ScreenSharePermission) -> String {
        switch permission {
        case .screenRecording: return L("settings.screenShare.permissions.screenRecordingBody")
        case .accessibility: return L("settings.screenShare.permissions.accessibilityBody")
        case .localNetwork: return L("settings.screenShare.permissions.localNetworkBody")
        case .notifications: return L("settings.screenShare.permissions.notificationsBody")
        }
    }
}
