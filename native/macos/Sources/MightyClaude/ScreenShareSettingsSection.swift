import MightyCore
import SwiftUI

/// Settings → 휴대폰: the BETA screen-share allow-list, one row per paired phone.
///
/// Everything here only asks the host. The allow-list, the view/control grant
/// and the enrolled key are enforced by `ScreenShareService`, so a row that
/// looks on but failed to save is put right by the next read rather than
/// trusted.
struct ScreenShareSettingsSection: View {
    @EnvironmentObject private var store: AppStore
    @ViewState private var rows: [String: ScreenShareDeviceSettings] = [:]
    @ViewState private var sessions: [ScreenShareLiveSession] = []
    @ViewState private var grantingControl: MobileDeviceInfo?
    @ViewState private var removingKey: MobileDeviceInfo?
    @ViewState private var showsPermissions = false
    /// The onboarding screen comes up by itself once: the first time a phone is
    /// allowed while a permission is still missing. After that it is a button.
    @AppStorage("screenShare.permissionOnboardingShown") private var onboardingShown = false

    private var devices: [MobileDeviceInfo] { store.mobileStatus.devices }

    var body: some View {
        SettingsGroup {
            Text(L("settings.screenShare.description"))
                .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Button { showsPermissions = true } label: {
                    Label(L("settings.screenShare.permissions.openButton"), systemImage: "lock.shield")
                }
                .controlSize(.small)
                .accessibilityIdentifier("settings-screen-share-permissions")
                Button { store.playScreenShareReferenceScene() } label: {
                    Label(L("settings.screenShare.scene.button"), systemImage: "play.rectangle")
                }
                .controlSize(.small)
                .accessibilityIdentifier("settings-screen-share-scene")
                Spacer()
            }
            Text(L("settings.screenShare.scene.description"))
                .font(.system(size: 10)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            if devices.isEmpty {
                Text(L("settings.screenShare.noPhones")).font(.system(size: 11)).foregroundStyle(.secondary)
            }
            ForEach(devices) { device in
                if device.legacy { legacyRow(device) } else { phoneRow(device) }
            }
        } header: {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Text(L("settings.screenShare.sectionTitle"))
                BetaBadge()
            }
        }
        .accessibilityIdentifier("settings-screen-share")
        .sheet(isPresented: $showsPermissions) { ScreenSharePermissionsView() }
        // The host's own state, read again and again while this is on screen: a
        // session started, ended or was killed from the menu bar meanwhile.
        .task {
            while !Task.isCancelled {
                await reload()
                try? await Task.sleep(for: .seconds(1))
            }
        }
        .confirmationDialog(L("settings.screenShare.controlTitle"),
                            isPresented: Binding(get: { grantingControl != nil }, set: { if !$0 { grantingControl = nil } }),
                            presenting: grantingControl) { device in
            Button(L("settings.screenShare.controlConfirm")) {
                setGrant(device.id, .control)
                grantingControl = nil
            }
            Button(L("settings.mobileRemote.cancelButton"), role: .cancel) { grantingControl = nil }
        } message: { device in
            Text(L("settings.screenShare.controlBody", ["device": device.name]))
        }
        .confirmationDialog(L("settings.screenShare.removeKeyTitle"),
                            isPresented: Binding(get: { removingKey != nil }, set: { if !$0 { removingKey = nil } }),
                            presenting: removingKey) { device in
            Button(L("settings.screenShare.removeKeyConfirm"), role: .destructive) {
                store.removeScreenShareControlKey(deviceId: device.id)
                rows[device.id]?.controlKeyPublicData = nil
                removingKey = nil
            }
            Button(L("settings.mobileRemote.cancelButton"), role: .cancel) { removingKey = nil }
        } message: { device in
            Text(L("settings.screenShare.removeKeyBody", ["device": device.name]))
        }
    }

    // MARK: Rows

    /// A phone that authenticates with the pairing key alone cannot be told
    /// apart from any other such phone, so it can never be allow-listed.
    private func legacyRow(_ device: MobileDeviceInfo) -> some View {
        HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
            Image(systemName: "questionmark.app").foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                Text(device.name).font(.system(size: 12))
                Text(L("settings.screenShare.legacyUnsupported"))
                    .font(.system(size: 10)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
        }
        .accessibilityIdentifier("settings-screen-share-legacy")
    }

    private func phoneRow(_ device: MobileDeviceInfo) -> some View {
        let row = rows[device.id] ?? ScreenShareDeviceSettings(deviceId: device.id)
        let live = sessions.filter { $0.deviceId == device.id }
        return VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: "iphone").foregroundStyle(live.isEmpty ? Color.secondary : Palette.doneText)
                Text(device.name).font(.system(size: 12))
                Spacer(minLength: 8)
                Toggle(L("settings.screenShare.allowToggle"), isOn: Binding(
                    get: { row.allowed },
                    set: { setAllowed(device.id, $0) }))
                    .toggleStyle(.switch).controlSize(.small)
                    .accessibilityIdentifier("settings-screen-share-allow-\(device.id)")
            }
            if row.allowed {
                Picker(L("settings.screenShare.grantLabel"), selection: Binding(
                    get: { row.grant },
                    set: { grant in
                        // Control is the one grant that needs the key ceremony
                        // explained before it is given.
                        if grant == .control, row.grant != .control { grantingControl = device }
                        else { setGrant(device.id, grant) }
                    })) {
                    Text(L("settings.screenShare.grantNone")).tag(ScreenShareGrant.none)
                    Text(L("settings.screenShare.grantView")).tag(ScreenShareGrant.view)
                    Text(L("settings.screenShare.grantControl")).tag(ScreenShareGrant.control)
                }
                .pickerStyle(.segmented).controlSize(.small)
                .accessibilityIdentifier("settings-screen-share-grant-\(device.id)")
                if row.grant == .control { keyLine(device, row: row) }
            }
            if live.isEmpty {
                Text(L("settings.screenShare.noSession")).font(.system(size: 10)).foregroundStyle(.secondary)
            }
            ForEach(live, id: \.sessionId) { session in
                HStack(spacing: DesignMetrics.Spacing.sm) {
                    Circle().fill(Palette.doneText).frame(width: 6, height: 6)
                    Text(session.mode == .control
                         ? L("settings.screenShare.sessionControl", ["time": Self.clock(session.startedAt)])
                         : L("settings.screenShare.sessionView", ["time": Self.clock(session.startedAt)]))
                        .font(.system(size: 11))
                    Spacer(minLength: 8)
                    Button(L("settings.screenShare.stopButton"), role: .destructive) {
                        store.stopScreenShareSession(sessionId: session.sessionId)
                        sessions.removeAll { $0.sessionId == session.sessionId }
                    }
                    .controlSize(.small)
                    .accessibilityIdentifier("settings-screen-share-stop-\(session.sessionId)")
                }
            }
        }
        .padding(.vertical, DesignMetrics.Spacing.xxs)
        .accessibilityIdentifier("settings-screen-share-device-\(device.id)")
    }

    @ViewBuilder private func keyLine(_ device: MobileDeviceInfo, row: ScreenShareDeviceSettings) -> some View {
        if let key = row.controlKeyPublicData {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: "key").font(.system(size: 10)).foregroundStyle(.secondary)
                Text(L("settings.screenShare.keyFingerprint", ["fingerprint": ScreenShareControlKey.fingerprint(key)]))
                    .font(.system(size: 11, design: .monospaced)).textSelection(.enabled)
                Spacer(minLength: 8)
                Button(L("settings.screenShare.removeKeyButton")) { removingKey = device }
                    .controlSize(.small)
                    .accessibilityIdentifier("settings-screen-share-remove-key-\(device.id)")
            }
        } else {
            Text(L("settings.screenShare.keyMissing"))
                .font(.system(size: 10)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
    }

    // MARK: Actions

    private func setAllowed(_ deviceId: String, _ allowed: Bool) {
        store.setScreenShareAllowed(deviceId: deviceId, allowed: allowed)
        var row = rows[deviceId] ?? ScreenShareDeviceSettings(deviceId: deviceId)
        row.allowed = allowed
        if !allowed { row.grant = .none; row.controlKeyPublicData = nil }
        rows[deviceId] = row
        guard allowed, !onboardingShown else { return }
        Task {
            let statuses = await ScreenSharePermissionReader.readQuick()
            guard ScreenSharePermissionReading.needsOnboarding(statuses), !onboardingShown else { return }
            onboardingShown = true
            showsPermissions = true
        }
    }

    private func setGrant(_ deviceId: String, _ grant: ScreenShareGrant) {
        store.setScreenShareGrant(deviceId: deviceId, grant: grant)
        rows[deviceId]?.grant = grant
        if grant != .control { rows[deviceId]?.controlKeyPublicData = nil }
    }

    private func reload() async {
        let list = await store.screenShare.allowList()
        let live = await store.screenShare.liveSessions()
        rows = Dictionary(list.map { ($0.deviceId, $0) }, uniquingKeysWith: { _, last in last })
        sessions = live.sorted { $0.startedAt < $1.startedAt }
    }

    private static func clock(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm:ss"
        return formatter.string(from: date)
    }
}
