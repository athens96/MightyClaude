import AppKit
import MightyCore
import SwiftUI

/// Settings section for phone access through the relay: switch, relay
/// address, connection state, pairing QR, key.
struct MobileRemoteSettingsSection: View {
    @EnvironmentObject private var store: AppStore
    @ViewState private var relayText = ""
    @ViewState private var showsKey = false
    @ViewState private var revoking: MobileDeviceInfo?

    private var settings: MobileRemoteSettings { store.snapshot.mobileRemote ?? MobileRemoteSettings() }
    private var status: MobileHostStatus { store.mobileStatus }
    private var relayDirty: Bool { RelayEndpoint.normalize(relayText) != RelayEndpoint.normalize(settings.relayURL) }

    var body: some View {
        Section(L("settings.mobileRemote.sectionTitle")) {
            Toggle(isOn: Binding(get: { settings.enabled }, set: { store.setMobileRemote(enabled: $0) })) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(L("settings.mobileRemote.allowToggle"))
                    Text(L("settings.mobileRemote.allowDescription"))
                        .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
            }
            .disabled(store.mobileBusy)
            .accessibilityIdentifier("settings-mobile-toggle")
            HStack(spacing: 8) {
                Text(L("settings.mobileRemote.relayLabel"))
                TextField("wss://relay.example.com", text: $relayText).textFieldStyle(.roundedBorder).font(.system(size: 11, design: .monospaced))
                    .onSubmit(applyRelay).accessibilityIdentifier("settings-mobile-relay")
                Button(L("settings.mobileRemote.applyButton"), action: applyRelay).disabled(store.mobileBusy || !relayDirty || RelayEndpoint.normalize(relayText) == nil)
            }
            HStack(spacing: 8) {
                Circle().fill(status.relayConnected ? Color.green : settings.enabled ? Color.orange : Color.secondary.opacity(0.4)).frame(width: 8, height: 8)
                Text(status.relayConnected
                     ? (status.clients > 0 ? L("settings.mobileRemote.statusConnectedTemplate", ["count": "\(status.clients)"]) : L("settings.mobileRemote.statusRelayConnected"))
                     : settings.enabled ? L("settings.mobileRemote.statusConnecting") : L("settings.mobileRemote.statusOff"))
                    .font(.system(size: 11)).foregroundStyle(.secondary)
                Spacer()
                if settings.enabled, !status.relayConnected {
                    Button(L("settings.mobileRemote.reconnectButton")) { Task { await store.mobileRemote.retryIfNeeded(); store.refreshMobileStatus() } }.controlSize(.small).disabled(store.mobileBusy)
                }
            }
            Text(status.detail).font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            if settings.enabled, RelayEndpoint.normalize(settings.relayURL) == nil {
                Text(L("settings.mobileRemote.relayHint")).font(.system(size: 11)).foregroundStyle(.orange).fixedSize(horizontal: false, vertical: true)
            }
            if status.relayConnected, let pairing = status.pairingURL {
                HStack(alignment: .top, spacing: 16) {
                    if let image = MobilePairingQR.image(for: pairing) {
                        Image(nsImage: image).interpolation(.none).resizable().frame(width: 160, height: 160)
                            .background(Color.white).clipShape(RoundedRectangle(cornerRadius: 8))
                            .accessibilityLabel(L("settings.mobileRemote.qrAccessibility")).accessibilityIdentifier("settings-mobile-qr")
                    }
                    VStack(alignment: .leading, spacing: 6) {
                        Text(L("settings.mobileRemote.scanInstruction")).font(.system(size: 11)).foregroundStyle(.secondary)
                        LabeledContent(L("settings.mobileRemote.hostIdLabel")) { Text(status.serverId).font(.system(size: 11, design: .monospaced)).textSelection(.enabled).lineLimit(1) }
                        LabeledContent(L("settings.mobileRemote.keyLabel")) {
                            HStack(spacing: 6) {
                                Text(showsKey ? (status.key ?? "") : String(repeating: "•", count: 16)).font(.system(size: 11, design: .monospaced)).textSelection(.enabled).lineLimit(1)
                                Button(showsKey ? L("settings.mobileRemote.hideKeyButton") : L("settings.mobileRemote.showKeyButton")) { showsKey.toggle() }.controlSize(.small)
                            }
                        }
                        HStack(spacing: 8) {
                            Button {
                                NSPasteboard.general.clearContents(); NSPasteboard.general.setString(pairing, forType: .string)
                            } label: { Label(L("settings.mobileRemote.copyLinkButton"), systemImage: "doc.on.doc") }.controlSize(.small)
                            Button(role: .destructive) { store.regenerateMobileKey() } label: { Label(L("settings.mobileRemote.regenerateKeyButton"), systemImage: "arrow.clockwise") }
                                .controlSize(.small).disabled(store.mobileBusy)
                                .help(L("settings.mobileRemote.regenerateKeyHelp"))
                        }
                    }
                }
                .padding(.vertical, 4)
            }
            Toggle(isOn: Binding(get: { settings.allowLegacyPhones }, set: { store.setMobileRemote(enabled: settings.enabled, allowLegacyPhones: $0) })) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(L("settings.mobileRemote.legacyAppsToggle"))
                    Text(L("settings.mobileRemote.legacyAppsDescription"))
                        .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
            }
            .disabled(store.mobileBusy)
            .accessibilityIdentifier("settings-mobile-legacy-toggle")
            if let warning = status.registryWarning {
                Text(warning).font(.system(size: 11)).foregroundStyle(.orange).fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("settings-mobile-registry-warning")
            }
            if !status.devices.isEmpty { devices }
        }
        .onAppear { relayText = settings.relayURL; store.refreshMobileStatus() }
        .confirmationDialog(L("settings.mobileRemote.revokeDialogTitle"), isPresented: Binding(get: { revoking != nil }, set: { if !$0 { revoking = nil } }), presenting: revoking) { device in
            Button(L("settings.mobileRemote.revokeConfirmButton"), role: .destructive) { store.revokeMobileDevice(device.id); revoking = nil }
            Button(L("settings.mobileRemote.cancelButton"), role: .cancel) { revoking = nil }
        } message: { device in
            Text(Self.revokeMessage(device, hasLegacy: status.devices.contains(where: \.legacy)))
        }
    }

    /// Built outside the view builder: the legacy row stands for several phones
    /// and has no token, so the two cases cannot share one sentence.
    static func revokeMessage(_ device: MobileDeviceInfo, hasLegacy: Bool) -> String {
        var message: String
        if device.legacy { message = L("settings.mobileRemote.revokeBodyLegacyTemplate", ["name": device.name]) }
        else { message = L("settings.mobileRemote.revokeBodyTokenTemplate", ["name": device.name]) }
        message += L("settings.mobileRemote.revokeBodyKeyReset")
        // The key is what a "구버전 앱" phone authenticates with every time, so
        // rotating it re-pairs the whole group — even when another row is the
        // one being unpaired.
        if hasLegacy { message += L("settings.mobileRemote.revokeBodyLegacyAllTemplate", ["name": MobileDeviceRegistry.legacyName]) }
        return message
    }

    /// The phones this host has admitted. A "구버전 앱" row stands for every
    /// app that predates device tokens, because such phones cannot be told apart.
    @ViewBuilder private var devices: some View {
        Text(L("settings.mobileRemote.connectedDevicesTitle")).font(.system(size: 11, weight: .medium)).foregroundStyle(.secondary)
        ForEach(status.devices) { device in
            HStack(spacing: 8) {
                Image(systemName: device.legacy ? "questionmark.app" : "iphone").foregroundStyle(device.connected ? Color.green : .secondary)
                VStack(alignment: .leading, spacing: 1) {
                    HStack(spacing: 6) {
                        Text(device.name).font(.system(size: 12))
                        if device.connected { connectedBadge }
                        if device.isNew { newBadge }
                    }
                    Text(seenLabel(device)).font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1)
                }
                Spacer(minLength: 8)
                Button(L("settings.mobileRemote.revokeRowButton")) { revoking = device }
                    .controlSize(.small).disabled(store.mobileBusy)
                    .accessibilityIdentifier("settings-mobile-revoke-\(device.id)")
            }
            .accessibilityIdentifier("settings-mobile-device-\(device.id)")
        }
    }

    private var connectedBadge: some View {
        Text(L("settings.mobileRemote.connectedBadge")).font(.system(size: 9, weight: .medium)).foregroundStyle(Color.green)
            .padding(.horizontal, 5).padding(.vertical, 1)
            .background(Color.green.opacity(0.15), in: Capsule())
    }

    /// A phone registered in the last day. An arrival the user did not make
    /// themselves is the one thing this list has to show at a glance.
    private var newBadge: some View {
        Text(L("settings.mobileRemote.newDeviceBadge")).font(.system(size: 9, weight: .medium)).foregroundStyle(Color.orange)
            .padding(.horizontal, 5).padding(.vertical, 1)
            .background(Color.orange.opacity(0.15), in: Capsule())
            .accessibilityIdentifier("settings-mobile-new-badge")
    }

    private func seenLabel(_ device: MobileDeviceInfo) -> String {
        L("settings.mobileRemote.seenTemplate", ["first": Self.stamp(device.firstSeen), "last": Self.stamp(device.lastSeen)])
    }
    private static func stamp(_ value: String) -> String {
        guard let date = AgentRunTiming.parseTimestamp(value) else { return L("settings.mobileRemote.unknownTime") }
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd HH:mm"
        return formatter.string(from: date)
    }

    private func applyRelay() {
        guard let relay = RelayEndpoint.normalize(relayText) else { return }
        relayText = relay
        store.setMobileRemote(enabled: settings.enabled, relayURL: relay)
    }
}
