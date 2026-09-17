import AppKit
import MightyCore
import SwiftUI

/// Settings section for phone access: switch, port, pairing QR, key.
struct MobileRemoteSettingsSection: View {
    @EnvironmentObject private var store: AppStore
    @ViewState private var portText = ""
    @ViewState private var showsKey = false

    private var settings: MobileRemoteSettings { store.snapshot.mobileRemote ?? MobileRemoteSettings() }
    private var status: MobileHostStatus { store.mobileStatus }

    var body: some View {
        Section("모바일 리모트") {
            Toggle(isOn: Binding(get: { settings.enabled }, set: { store.setMobileRemote(enabled: $0) })) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("휴대폰에서 이 Mac에 연결 허용")
                    Text("Tailscale 네트워크 안에서만 열립니다. 켜 둔 상태는 앱을 다시 실행해도 유지됩니다.").font(.system(size: 11)).foregroundStyle(.secondary)
                }
            }
            .disabled(store.mobileBusy)
            .accessibilityIdentifier("settings-mobile-toggle")
            HStack(spacing: 8) {
                Text("포트")
                TextField("43138", text: $portText).frame(width: 80).textFieldStyle(.roundedBorder).monospacedDigit()
                    .onSubmit(applyPort).accessibilityIdentifier("settings-mobile-port")
                Button("적용", action: applyPort).disabled(store.mobileBusy || Int(portText) == settings.port)
                Spacer()
                Circle().fill(status.listening ? Color.green : settings.enabled ? Color.orange : Color.secondary.opacity(0.4)).frame(width: 8, height: 8)
                Text(status.listening ? "연결 대기 중" : settings.enabled ? "대기" : "꺼짐").font(.system(size: 11)).foregroundStyle(.secondary)
            }
            Text(status.detail).font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            if status.listening, let pairing = status.pairingURL, let address = status.address {
                HStack(alignment: .top, spacing: 16) {
                    if let image = MobilePairingQR.image(for: pairing) {
                        Image(nsImage: image).interpolation(.none).resizable().frame(width: 160, height: 160)
                            .background(Color.white).clipShape(RoundedRectangle(cornerRadius: 8))
                            .accessibilityLabel("페어링 QR 코드").accessibilityIdentifier("settings-mobile-qr")
                    }
                    VStack(alignment: .leading, spacing: 6) {
                        Text("휴대폰 앱에서 QR을 스캔하거나 아래 값을 입력하세요.").font(.system(size: 11)).foregroundStyle(.secondary)
                        LabeledContent("주소") { Text(address).font(.system(size: 11, design: .monospaced)).textSelection(.enabled) }
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
        .onAppear { portText = String(settings.port); store.refreshMobileStatus() }
    }

    private func applyPort() {
        guard let port = Int(portText), (1024...65535).contains(port) else { portText = String(settings.port); return }
        store.setMobileRemote(enabled: settings.enabled, port: port)
    }
}
