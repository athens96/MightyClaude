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
        Section("모바일 리모트") {
            Toggle(isOn: Binding(get: { settings.enabled }, set: { store.setMobileRemote(enabled: $0) })) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("휴대폰에서 이 Mac에 연결 허용")
                    Text("Mac과 휴대폰이 각각 릴레이 서버에 접속해 연결됩니다. 포트 개방이나 VPN이 필요 없고, 릴레이는 암호문만 전달합니다. 켜 둔 상태는 앱을 다시 실행해도 유지됩니다.")
                        .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
            }
            .disabled(store.mobileBusy)
            .accessibilityIdentifier("settings-mobile-toggle")
            HStack(spacing: 8) {
                Text("릴레이")
                TextField("wss://relay.example.com", text: $relayText).textFieldStyle(.roundedBorder).font(.system(size: 11, design: .monospaced))
                    .onSubmit(applyRelay).accessibilityIdentifier("settings-mobile-relay")
                Button("적용", action: applyRelay).disabled(store.mobileBusy || !relayDirty || RelayEndpoint.normalize(relayText) == nil)
            }
            HStack(spacing: 8) {
                Circle().fill(status.relayConnected ? Color.green : settings.enabled ? Color.orange : Color.secondary.opacity(0.4)).frame(width: 8, height: 8)
                Text(status.relayConnected ? (status.clients > 0 ? "연결됨 · 휴대폰 \(status.clients)대" : "릴레이 연결됨 · 휴대폰 대기") : settings.enabled ? "릴레이 연결 중" : "꺼짐")
                    .font(.system(size: 11)).foregroundStyle(.secondary)
                Spacer()
                if settings.enabled, !status.relayConnected {
                    Button("다시 연결") { Task { await store.mobileRemote.retryIfNeeded(); store.refreshMobileStatus() } }.controlSize(.small).disabled(store.mobileBusy)
                }
            }
            Text(status.detail).font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            if settings.enabled, RelayEndpoint.normalize(settings.relayURL) == nil {
                Text("릴레이 서버 주소를 입력하세요. 저장소의 relay/ 폴더로 직접 띄울 수 있습니다(docs/relay.md).").font(.system(size: 11)).foregroundStyle(.orange).fixedSize(horizontal: false, vertical: true)
            }
            if status.relayConnected, let pairing = status.pairingURL {
                HStack(alignment: .top, spacing: 16) {
                    if let image = MobilePairingQR.image(for: pairing) {
                        Image(nsImage: image).interpolation(.none).resizable().frame(width: 160, height: 160)
                            .background(Color.white).clipShape(RoundedRectangle(cornerRadius: 8))
                            .accessibilityLabel("페어링 QR 코드").accessibilityIdentifier("settings-mobile-qr")
                    }
                    VStack(alignment: .leading, spacing: 6) {
                        Text("휴대폰 앱에서 QR을 스캔하거나 페어링 링크를 붙여 넣으세요.").font(.system(size: 11)).foregroundStyle(.secondary)
                        LabeledContent("호스트 ID") { Text(status.serverId).font(.system(size: 11, design: .monospaced)).textSelection(.enabled).lineLimit(1) }
                        LabeledContent("키") {
                            HStack(spacing: 6) {
                                Text(showsKey ? (status.key ?? "") : String(repeating: "•", count: 16)).font(.system(size: 11, design: .monospaced)).textSelection(.enabled).lineLimit(1)
                                Button(showsKey ? "숨기기" : "보기") { showsKey.toggle() }.controlSize(.small)
                            }
                        }
                        HStack(spacing: 8) {
                            Button {
                                NSPasteboard.general.clearContents(); NSPasteboard.general.setString(pairing, forType: .string)
                            } label: { Label("페어링 링크 복사", systemImage: "doc.on.doc") }.controlSize(.small)
                            Button(role: .destructive) { store.regenerateMobileKey() } label: { Label("키 다시 만들기", systemImage: "arrow.clockwise") }
                                .controlSize(.small).disabled(store.mobileBusy)
                                .help("QR 코드가 바뀝니다. 기기 토큰을 가진 휴대폰은 그대로 연결되고, 토큰을 모르는 구버전 앱만 다시 페어링하면 됩니다.")
                        }
                    }
                }
                .padding(.vertical, 4)
            }
            Toggle(isOn: Binding(get: { settings.allowLegacyPhones }, set: { store.setMobileRemote(enabled: settings.enabled, allowLegacyPhones: $0) })) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("구버전 앱(기기 토큰 없음) 허용")
                    Text("끄면 기기 토큰을 쓰는 앱만 연결할 수 있습니다. 토큰이 있는 휴대폰만 개별 해제가 가능하므로, 모든 휴대폰을 최신 앱으로 올린 뒤 끄는 것을 권합니다. 지금 키로만 연결된 휴대폰은 즉시 끊깁니다.")
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
        .confirmationDialog("이 기기의 연결을 해제할까요?", isPresented: Binding(get: { revoking != nil }, set: { if !$0 { revoking = nil } }), presenting: revoking) { device in
            Button("해제", role: .destructive) { store.revokeMobileDevice(device.id); revoking = nil }
            Button("취소", role: .cancel) { revoking = nil }
        } message: { device in
            Text(Self.revokeMessage(device, hasLegacy: status.devices.contains(where: \.legacy)))
        }
    }

    /// Built outside the view builder: the legacy row stands for several phones
    /// and has no token, so the two cases cannot share one sentence.
    static func revokeMessage(_ device: MobileDeviceInfo, hasLegacy: Bool) -> String {
        var message: String
        if device.legacy { message = "\(device.name)으로 묶인 휴대폰이 모두 끊깁니다." }
        else { message = "\(device.name)의 기기 토큰이 무효가 되어 즉시 끊깁니다." }
        message += " 페어링 키도 새로 만들어지므로 QR 코드가 바뀌고, 이 기기를 다시 쓰려면 새 QR로 페어링해야 합니다. 토큰을 가진 다른 기기는 그대로 연결됩니다."
        // The key is what a "구버전 앱" phone authenticates with every time, so
        // rotating it re-pairs the whole group — even when another row is the
        // one being unpaired.
        if hasLegacy { message += " \(MobileDeviceRegistry.legacyName)으로 묶인 휴대폰은 모두 새 QR로 다시 페어링해야 합니다." }
        return message
    }

    /// The phones this host has admitted. A "구버전 앱" row stands for every
    /// app that predates device tokens, because such phones cannot be told apart.
    @ViewBuilder private var devices: some View {
        Text("연결된 기기").font(.system(size: 11, weight: .medium)).foregroundStyle(.secondary)
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
                Button("해제") { revoking = device }
                    .controlSize(.small).disabled(store.mobileBusy)
                    .accessibilityIdentifier("settings-mobile-revoke-\(device.id)")
            }
            .accessibilityIdentifier("settings-mobile-device-\(device.id)")
        }
    }

    private var connectedBadge: some View {
        Text("연결됨").font(.system(size: 9, weight: .medium)).foregroundStyle(Color.green)
            .padding(.horizontal, 5).padding(.vertical, 1)
            .background(Color.green.opacity(0.15), in: Capsule())
    }

    /// A phone registered in the last day. An arrival the user did not make
    /// themselves is the one thing this list has to show at a glance.
    private var newBadge: some View {
        Text("새 기기").font(.system(size: 9, weight: .medium)).foregroundStyle(Color.orange)
            .padding(.horizontal, 5).padding(.vertical, 1)
            .background(Color.orange.opacity(0.15), in: Capsule())
            .accessibilityIdentifier("settings-mobile-new-badge")
    }

    private func seenLabel(_ device: MobileDeviceInfo) -> String {
        "처음 \(Self.stamp(device.firstSeen)) · 마지막 \(Self.stamp(device.lastSeen))"
    }
    private static func stamp(_ value: String) -> String {
        guard let date = AgentRunTiming.parseTimestamp(value) else { return "알 수 없음" }
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
