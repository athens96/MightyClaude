import SwiftUI
import MightyCore

struct AppUpdateSettingsSection: View {
    @EnvironmentObject private var store: AppStore

    var body: some View {
        Section(L("settings.appUpdate.sectionTitle")) {
            LabeledContent(L("settings.appUpdate.currentVersionLabel"), value: store.appVersion)
            if store.appUpdatePublicKey == nil {
                // Rule 1: keyless builds do not support update checks — no bypass.
                Text(L("settings.appUpdate.noPublicKeyNotice"))
                    .font(.system(size: 11)).foregroundStyle(.orange)
                HStack(spacing: 8) {
                    Spacer()
                    Button(L("settings.appUpdate.checkButton")) {}.disabled(true)
                }
            } else {
                // Rule 3: when the build carries a URL it is the only address; show it read-only.
                if let builtIn = store.builtInUpdateManifestURL {
                    LabeledContent(L("settings.appUpdate.manifestUrlAddressLabel")) {
                        Text(builtIn).font(.system(size: 11, design: .monospaced)).foregroundStyle(.secondary).textSelection(.enabled)
                    }
                } else {
                    TextField(L("settings.appUpdate.manifestUrlPlaceholder"), text: Binding(get: { store.appUpdateManifestURLOverride }, set: { store.appUpdateManifestURLOverride = $0 }))
                        .textFieldStyle(.roundedBorder).font(.system(size: 12, design: .monospaced))
                        .accessibilityIdentifier("app-update-url")
                    if store.appUpdateManifestURL == nil {
                        Text(L("settings.appUpdate.manifestUrlHint")).font(.system(size: 11)).foregroundStyle(.secondary)
                    }
                }
                Toggle(L("settings.appUpdate.autoCheckToggle"), isOn: Binding(get: { store.appUpdateAutomatic }, set: { store.appUpdateAutomatic = $0 }))
                    .accessibilityIdentifier("app-update-automatic")
                Text(L("settings.appUpdate.signatureVerified"))
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
            Text(store.appUpdate.checkedAt.map {
                L("settings.appUpdate.lastCheckedTemplate", ["time": $0.formatted(date: .omitted, time: .shortened)])
            } ?? L("settings.appUpdate.notCheckedYet")).font(.system(size: 11)).foregroundStyle(.secondary)
        case .checking:
            ProgressView().controlSize(.small); Text(L("settings.appUpdate.checking")).font(.system(size: 11))
        case .upToDate:
            Label(L("settings.appUpdate.upToDate"), systemImage: "checkmark.circle").font(.system(size: 11)).foregroundStyle(.secondary)
        case .available:
            Label(L("settings.appUpdate.availableTemplate", ["version": store.appUpdate.availability?.manifest.version ?? ""]), systemImage: "arrow.down.circle").font(.system(size: 11, weight: .medium)).foregroundStyle(Palette.accent)
        case .downloading(let fraction):
            ProgressView(value: fraction).frame(width: 140); Text(L("settings.appUpdate.downloadingTemplate", ["percent": "\(Int(fraction * 100))"])).font(.system(size: 11))
        case .staging:
            ProgressView().controlSize(.small); Text(L("settings.appUpdate.stagingProgress")).font(.system(size: 11))
        case .ready:
            Label(L("settings.appUpdate.readyTemplate", ["version": store.appUpdate.availability?.manifest.version ?? ""]), systemImage: "shippingbox").font(.system(size: 11)).foregroundStyle(.secondary)
        case .installing:
            ProgressView().controlSize(.small); Text(L("settings.appUpdate.installing")).font(.system(size: 11))
        case .failed(let message):
            Label(message, systemImage: "exclamationmark.triangle").font(.system(size: 11)).foregroundStyle(.orange).lineLimit(3).textSelection(.enabled)
        }
    }

    @ViewBuilder private var actionButton: some View {
        switch store.appUpdate.phase {
        case .available:
            Button(L("settings.appUpdate.downloadButton")) { store.downloadAppUpdate() }.accessibilityIdentifier("app-update-download")
        case .downloading:
            Button(L("settings.appUpdate.cancelButton")) { store.cancelAppUpdateDownload() }
        case .ready:
            Button(L("settings.appUpdate.installButton")) { store.installAppUpdateAndRelaunch() }.accessibilityIdentifier("app-update-install")
        case .checking, .installing, .staging:
            Button(L("settings.appUpdate.inProgressButton")) {}.disabled(true)
        default:
            Button(L("settings.appUpdate.checkButton")) { store.checkForAppUpdate() }.disabled(store.appUpdateManifestURL == nil).accessibilityIdentifier("app-update-check")
        }
    }
}
