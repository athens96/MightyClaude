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
    @ViewState private var portText = ""

    private var settings: MobileRemoteSettings { store.snapshot.mobileRemote ?? MobileRemoteSettings() }
    private var status: MobileHostStatus { store.mobileStatus }
    private var relayDirty: Bool { RelayEndpoint.normalize(relayText) != RelayEndpoint.normalize(settings.relayURL) }
    private var typedPort: Int? { Int(portText.trimmingCharacters(in: .whitespaces)).flatMap { MobileDirect.validPort($0) ? $0 : nil } }

    var body: some View {
        SettingsGroup(L("settings.mobileRemote.sectionTitle")) {
            Toggle(isOn: Binding(get: { settings.enabled }, set: { store.setMobileRemote(enabled: $0) })) {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                    Text(L("settings.mobileRemote.allowToggle"))
                    Text(L("settings.mobileRemote.allowDescription"))
                        .font(.ui(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
            }
            .disabled(store.mobileBusy)
            .accessibilityIdentifier("settings-mobile-toggle")
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Text(L("settings.mobileRemote.relayLabel"))
                TextField(MobileWire.defaultRelayURL.isEmpty ? "wss://relay.example.com" : MobileWire.defaultRelayURL, text: $relayText).textFieldStyle(.roundedBorder).font(.ui(size: 11, design: .monospaced))
                    .onSubmit(applyRelay).accessibilityIdentifier("settings-mobile-relay")
                Button(L("settings.mobileRemote.applyButton"), action: applyRelay).disabled(store.mobileBusy || !relayDirty || RelayEndpoint.normalize(relayText) == nil)
            }
            connectionGuide
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Circle().fill(Palette.mark(status.relayConnected ? "completed" : settings.enabled ? "waiting" : "idle")).frame(width: 8, height: 8)
                Text(status.relayConnected
                     ? (status.clients > 0 ? L("settings.mobileRemote.statusConnectedTemplate", ["count": "\(status.clients)"]) : L("settings.mobileRemote.statusRelayConnected"))
                     : settings.enabled ? L("settings.mobileRemote.statusConnecting") : L("settings.mobileRemote.statusOff"))
                    .font(.ui(size: 11)).foregroundStyle(.secondary)
                Spacer()
                // Shown while connected too: a socket a network change left
                // half-open still reads as connected until its ping times out.
                if settings.enabled, settings.effectiveRelayURL != nil {
                    Button(L("settings.mobileRemote.reconnectButton")) { Task { await store.mobileRemote.reconnect(); store.refreshMobileStatus() } }.controlSize(.small).disabled(store.mobileBusy)
                }
            }
            Text(status.detail).font(.ui(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                // What the host pushes can still go missing on the way; while
                // this is on screen, read the host's own state now and then.
                .task {
                    while !Task.isCancelled {
                        store.refreshMobileStatus()
                        try? await Task.sleep(for: .seconds(3))
                    }
                }
            if settings.enabled, settings.effectiveRelayURL == nil {
                Text(L("settings.mobileRemote.relayHint")).font(.ui(size: 11)).foregroundStyle(Palette.waitText).fixedSize(horizontal: false, vertical: true)
            }
            direct
            if let pairing = status.pairingURL {
                HStack(alignment: .top, spacing: DesignMetrics.Spacing.lg) {
                    if let image = MobilePairingQR.image(for: pairing) {
                        Image(nsImage: image).interpolation(.none).resizable().frame(width: 160, height: 160)
                            .background(Color.white).clipShape(RoundedRectangle(cornerRadius: 8))
                            .accessibilityLabel(L("settings.mobileRemote.qrAccessibility")).accessibilityIdentifier("settings-mobile-qr")
                    }
                    VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                        Text(L("settings.mobileRemote.scanInstruction")).font(.ui(size: 11)).foregroundStyle(.secondary)
                        LabeledContent(L("settings.mobileRemote.hostIdLabel")) { Text(status.serverId).font(.ui(size: 11, design: .monospaced)).textSelection(.enabled).lineLimit(1) }
                        LabeledContent(L("settings.mobileRemote.keyLabel")) {
                            HStack(spacing: DesignMetrics.Spacing.sm) {
                                Text(showsKey ? (status.key ?? "") : String(repeating: "•", count: 16)).font(.ui(size: 11, design: .monospaced)).textSelection(.enabled).lineLimit(1)
                                Button(showsKey ? L("settings.mobileRemote.hideKeyButton") : L("settings.mobileRemote.showKeyButton")) { showsKey.toggle() }.controlSize(.small)
                            }
                        }
                        HStack(spacing: DesignMetrics.Spacing.sm) {
                            Button {
                                NSPasteboard.general.clearContents(); NSPasteboard.general.setString(pairing, forType: .string)
                            } label: { Label(L("settings.mobileRemote.copyLinkButton"), systemImage: "doc.on.doc") }.controlSize(.small)
                            Button(role: .destructive) { store.regenerateMobileKey() } label: { Label(L("settings.mobileRemote.regenerateKeyButton"), systemImage: "arrow.clockwise") }
                                .controlSize(.small).disabled(store.mobileBusy)
                                .help(L("settings.mobileRemote.regenerateKeyHelp"))
                        }
                    }
                }
                .padding(.vertical, DesignMetrics.Spacing.xxs)
            }
            Toggle(isOn: Binding(get: { settings.allowLegacyPhones }, set: { store.setMobileRemote(enabled: settings.enabled, allowLegacyPhones: $0) })) {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                    Text(L("settings.mobileRemote.legacyAppsToggle"))
                    Text(L("settings.mobileRemote.legacyAppsDescription"))
                        .font(.ui(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
            }
            .disabled(store.mobileBusy)
            .accessibilityIdentifier("settings-mobile-legacy-toggle")
            if let warning = status.registryWarning {
                Text(warning).font(.ui(size: 11)).foregroundStyle(Palette.waitText).fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("settings-mobile-registry-warning")
            }
            if !status.devices.isEmpty { devices }
        }
        .onAppear { relayText = settings.relayURL; portText = String(settings.directPort); store.refreshMobileStatus() }
        .confirmationDialog(L("settings.mobileRemote.revokeDialogTitle"), isPresented: Binding(get: { revoking != nil }, set: { if !$0 { revoking = nil } }), presenting: revoking) { device in
            Button(L("settings.mobileRemote.revokeConfirmButton"), role: .destructive) { store.revokeMobileDevice(device.id); revoking = nil }
            Button(L("settings.mobileRemote.cancelButton"), role: .cancel) { revoking = nil }
        } message: { device in
            Text(Self.revokeMessage(device, hasLegacy: status.devices.contains(where: \.legacy)))
        }
    }

    /// The direct path: its switch, its port and what it found — the Tailscale
    /// endpoints it listens on, or that there is no Tailscale on this Mac.
    @ViewBuilder private var direct: some View {
        Toggle(isOn: Binding(get: { settings.directEnabled }, set: { store.setMobileRemote(enabled: settings.enabled, directEnabled: $0) })) {
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                Text(L("mobileRemote.direct.toggle"))
                Text(L("mobileRemote.direct.description"))
                    .font(.ui(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
        }
        .disabled(store.mobileBusy)
        .accessibilityIdentifier("settings-mobile-direct-toggle")
        if settings.directEnabled {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Text(L("mobileRemote.direct.portLabel"))
                TextField(String(MobileDirect.defaultPort), text: $portText).textFieldStyle(.roundedBorder).font(.ui(size: 11, design: .monospaced)).frame(width: 80)
                    .onSubmit(applyPort).accessibilityIdentifier("settings-mobile-direct-port")
                Button(L("settings.mobileRemote.applyButton"), action: applyPort).disabled(store.mobileBusy || typedPort == nil || typedPort == settings.directPort)
                Spacer()
            }
            if settings.enabled, !status.directDetail.isEmpty {
                HStack(spacing: DesignMetrics.Spacing.sm) {
                    Circle().fill(Palette.mark(status.directListening ? "completed" : "idle")).frame(width: 8, height: 8)
                    Text(status.directDetail).font(.ui(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                        .textSelection(.enabled)
                }
                .accessibilityIdentifier("settings-mobile-direct-status")
            }
        }
    }

    private func applyPort() {
        guard let port = typedPort, port != settings.directPort else { return }
        store.setMobileRemote(enabled: settings.enabled, directPort: port)
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
        Text(L("settings.mobileRemote.connectedDevicesTitle")).font(.ui(size: 11, weight: .medium)).foregroundStyle(.secondary)
        ForEach(status.devices) { device in
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: device.legacy ? "questionmark.app" : "iphone").foregroundStyle(device.connected ? Palette.doneText : .secondary)
                VStack(alignment: .leading, spacing: 1) {
                    HStack(spacing: DesignMetrics.Spacing.sm) {
                        Text(device.name).font(.ui(size: 12))
                        if device.connected { connectedBadge }
                        if device.isNew { newBadge }
                    }
                    Text(seenLabel(device)).font(.ui(size: 10)).foregroundStyle(.secondary).lineLimit(1)
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
        Text(L("settings.mobileRemote.connectedBadge")).font(.ui(size: 9, weight: .medium)).foregroundStyle(Palette.doneText)
            .padding(.horizontal, DesignMetrics.Spacing.xs).padding(.vertical, 1)
            .background(Palette.doneSoft, in: Capsule())
    }

    /// A phone registered in the last day. An arrival the user did not make
    /// themselves is the one thing this list has to show at a glance.
    private var newBadge: some View {
        Text(L("settings.mobileRemote.newDeviceBadge")).font(.ui(size: 9, weight: .medium)).foregroundStyle(Palette.waitText)
            .padding(.horizontal, DesignMetrics.Spacing.xs).padding(.vertical, 1)
            .background(Palette.waitSoft, in: Capsule())
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

    @ViewBuilder private var connectionGuide: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
            guideStep(1, L("settings.mobileRemote.guide.step1"))
            guideStep(2, L("settings.mobileRemote.guide.step2"))
            guideStep(3, L("settings.mobileRemote.guide.step3"))
            guideStep(4, L("settings.mobileRemote.guide.step4"))
        }
        .padding(.vertical, DesignMetrics.Spacing.xxs)
        .accessibilityIdentifier("settings-mobile-guide")
    }

    private func guideStep(_ n: Int, _ text: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: DesignMetrics.Spacing.xs) {
            Text("\(n).").font(.ui(size: 11)).foregroundStyle(.secondary).frame(width: 14, alignment: .trailing)
            Text(text).font(.ui(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
    }

    private func applyRelay() {
        guard let relay = RelayEndpoint.normalize(relayText) else { return }
        relayText = relay
        store.setMobileRemote(enabled: settings.enabled, relayURL: relay)
    }
}
