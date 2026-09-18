import AppKit
import MightyCore
import SwiftUI

/// Settings section for phone access through the relay: switch, relay
/// address, connection state, pairing QR, key.
struct MobileRemoteSettingsSection: View {
    @EnvironmentObject private var store: AppStore
    @ViewState private var relayText = ""
    @ViewState private var showsKey = false

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
                                .help("연결된 모든 휴대폰을 다시 페어링해야 합니다.")
                        }
                    }
                }
                .padding(.vertical, 4)
            }
        }
        .onAppear { relayText = settings.relayURL; store.refreshMobileStatus() }
    }

    private func applyRelay() {
        guard let relay = RelayEndpoint.normalize(relayText) else { return }
        relayText = relay
        store.setMobileRemote(enabled: settings.enabled, relayURL: relay)
    }
}
