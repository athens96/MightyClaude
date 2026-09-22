import SwiftUI
import MightyCore

struct AppUpdateSettingsSection: View {
    @EnvironmentObject private var store: AppStore

    var body: some View {
        Section("앱 업데이트") {
            LabeledContent("현재 버전", value: store.appVersion)
            if store.appUpdatePublicKey == nil {
                // Rule 1: keyless builds do not support update checks — no bypass.
                Text("이 빌드는 업데이트 확인을 지원하지 않습니다.")
                    .font(.system(size: 11)).foregroundStyle(.orange)
                HStack(spacing: 8) {
                    Spacer()
                    Button("업데이트 확인") {}.disabled(true)
                }
            } else {
                // Rule 3: when the build carries a URL it is the only address; show it read-only.
                if let builtIn = store.builtInUpdateManifestURL {
                    LabeledContent("업데이트 주소") {
                        Text(builtIn).font(.system(size: 11, design: .monospaced)).foregroundStyle(.secondary).textSelection(.enabled)
                    }
                } else {
                    TextField("업데이트 정보 주소 (https://…/latest.json)", text: Binding(get: { store.appUpdateManifestURLOverride }, set: { store.appUpdateManifestURLOverride = $0 }))
                        .textFieldStyle(.roundedBorder).font(.system(size: 12, design: .monospaced))
                        .accessibilityIdentifier("app-update-url")
                    if store.appUpdateManifestURL == nil {
                        Text("Cloudflare에 올린 latest.json의 https 주소를 입력하세요.").font(.system(size: 11)).foregroundStyle(.secondary)
                    }
                }
                Toggle("앱 시작 시 하루 한 번 새 버전 확인", isOn: Binding(get: { store.appUpdateAutomatic }, set: { store.appUpdateAutomatic = $0 }))
                    .accessibilityIdentifier("app-update-automatic")
                Text("서명 검증: 이 빌드에 포함된 공개 키로 서명된 업데이트 정보만 받습니다.")
                    .font(.system(size: 10)).foregroundStyle(.secondary)
                HStack(spacing: 8) {
                    statusView
                    Spacer()
                    actionButton
                }
                if let manifest = store.appUpdate.availability?.manifest, store.appUpdate.availability?.isNewer == true, let notes = manifest.notes, !notes.isEmpty {
                    Text(notes).font(.system(size: 11)).foregroundStyle(.secondary).textSelection(.enabled).lineLimit(8)
                }
            }
        }
    }

    @ViewBuilder private var statusView: some View {
        switch store.appUpdate.phase {
        case .idle:
            Text(store.appUpdate.checkedAt.map { "마지막 확인 \($0.formatted(date: .omitted, time: .shortened))" } ?? "아직 확인하지 않았습니다.").font(.system(size: 11)).foregroundStyle(.secondary)
        case .checking:
            ProgressView().controlSize(.small); Text("새 버전 확인 중…").font(.system(size: 11))
        case .upToDate:
            Label("최신 버전입니다.", systemImage: "checkmark.circle").font(.system(size: 11)).foregroundStyle(.secondary)
        case .available:
            Label("새 버전 \(store.appUpdate.availability?.manifest.version ?? "") 이 있습니다.", systemImage: "arrow.down.circle").font(.system(size: 11, weight: .medium)).foregroundStyle(Palette.accent)
        case .downloading(let fraction):
            ProgressView(value: fraction).frame(width: 140); Text("\(Int(fraction * 100))% 받는 중…").font(.system(size: 11))
        case .staging:
            ProgressView().controlSize(.small); Text("패키지를 풀고 확인하는 중…").font(.system(size: 11))
        case .ready:
            Label("\(store.appUpdate.availability?.manifest.version ?? "") 설치 준비 완료 · 설치하면 앱이 종료된 뒤 교체되고 다시 실행됩니다.", systemImage: "shippingbox").font(.system(size: 11)).foregroundStyle(.secondary)
        case .installing:
            ProgressView().controlSize(.small); Text("앱을 종료하고 교체하는 중…").font(.system(size: 11))
        case .failed(let message):
            Label(message, systemImage: "exclamationmark.triangle").font(.system(size: 11)).foregroundStyle(.orange).lineLimit(3).textSelection(.enabled)
        }
    }

    @ViewBuilder private var actionButton: some View {
        switch store.appUpdate.phase {
        case .available:
            Button("다운로드") { store.downloadAppUpdate() }.accessibilityIdentifier("app-update-download")
        case .downloading:
            Button("취소") { store.cancelAppUpdateDownload() }
        case .ready:
            Button("설치하고 다시 실행") { store.installAppUpdateAndRelaunch() }.accessibilityIdentifier("app-update-install")
        case .checking, .installing, .staging:
            Button("진행 중…") {}.disabled(true)
        default:
            Button("업데이트 확인") { store.checkForAppUpdate() }.disabled(store.appUpdateManifestURL == nil).accessibilityIdentifier("app-update-check")
        }
    }
}
