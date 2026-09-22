import SwiftUI
import MightyCore

/// Settings › 마이티 스타일: what was found, what was said yes to, and what
/// was refused with the reason why (§6.2).
struct StyleSettingsSection: View {
    @EnvironmentObject private var store: AppStore
    @ViewState private var candidate: StyleApprovalCandidate?
    @ViewState private var registerError: String?

    private var rows: [StyleSettingsRow] { StyleSettingsList.rows(store.styleRegistry.styles, locked: store.styleTrustLocked) }
    private var byPath: [String: RegisteredStyle] {
        Dictionary(store.styleRegistry.styles.map { ($0.path, $0) }, uniquingKeysWith: { first, _ in first })
    }

    var body: some View {
        Section {
            if store.styleTrustLocked { lockBanner }
            HStack(spacing: 8) {
                Button(L("settings.styles.registerButton")) { register() }.disabled(store.styleTrustLocked)
                    .accessibilityIdentifier("settings-style-register")
                Button(L("settings.styles.rescanButton")) { store.rescanStyles() }
                    .accessibilityIdentifier("settings-style-rescan")
                Spacer(minLength: 0)
            }
            if let registerError {
                Label(registerError, systemImage: "exclamationmark.triangle").font(.system(size: 11)).foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Text(L("settings.styles.description"))
                .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            ForEach(rows) { row in styleRow(row) }
            ForEach(store.styleRejections, id: \.path) { rejection in rejectionRow(rejection) }
        } header: {
            Text(L("settings.styles.sectionTitle"))
        }
        .sheet(item: $candidate) { item in
            StyleApprovalSheet(candidate: item, onClose: { candidate = nil }).environmentObject(store)
        }
    }

    private var lockBanner: some View {
        VStack(alignment: .leading, spacing: 3) {
            Label(StyleSettingsList.lockedMessage(store.styleTrustPath), systemImage: "exclamationmark.triangle")
                .font(.system(size: 11, weight: .medium)).foregroundStyle(.orange)
            Text(L("settings.styles.lockBanner"))
                .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
        .textSelection(.enabled)
        .accessibilityIdentifier("settings-style-locked")
    }

    private func styleRow(_ row: StyleSettingsRow) -> some View {
        let head = [row.name, row.badge].compactMap { $0 }.joined(separator: " " + StyleChrome.separator + " ")
        let detail = [
            row.styleId,
            L("settings.styles.hashDetailTemplate", ["hash": row.hashPrefix]),
            L("settings.styles.actionCountTemplate", ["count": "\(row.actionCount)"]),
            L("settings.styles.autoAllowCountTemplate", ["count": "\(row.autoAllowCount)"]),
        ].joined(separator: " " + StyleChrome.separator + " ")
        return VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 6) {
                Text(verbatim: head).font(.system(size: 12, weight: .medium))
                Text(verbatim: row.stateLabel).font(.system(size: 10)).foregroundStyle(.secondary)
                    .padding(.horizontal, 5).padding(.vertical, 1).background(Palette.subtle, in: Capsule())
                Spacer(minLength: 0)
            }
            Text(verbatim: detail).font(.system(size: 10)).foregroundStyle(.secondary)
            Text(verbatim: row.path).font(.system(size: 10, design: .monospaced)).foregroundStyle(.tertiary)
                .textSelection(.enabled).lineLimit(1).truncationMode(.middle)
            buttons(row)
        }
        .padding(.vertical, 3)
        .accessibilityElement(children: .contain).accessibilityIdentifier("settings-style-\(row.styleId)")
    }

    @ViewBuilder private func buttons(_ row: StyleSettingsRow) -> some View {
        let style = byPath[row.path]
        HStack(spacing: 6) {
            Button(L("settings.styles.viewButton")) {
                guard let style else { return }
                candidate = StyleApprovalCandidate(style: style, data: store.styleBytes(for: style), readOnly: true)
            }.controlSize(.small).disabled(style == nil)
            if row.canApprove {
                Button(L("settings.styles.allowButton")) {
                    guard let style else { return }
                    candidate = StyleApprovalCandidate(style: style, data: nil)
                }.controlSize(.small)
            }
            if row.canRevoke { Button(L("settings.styles.revokeButton")) { style.map { store.revokeStyle($0) } }.controlSize(.small) }
            // The button drops the refusal; the style then goes back through
            // the approval card like any other pending one (§4.2).
            if row.canAllowAgain { Button(L("settings.styles.unblockButton")) { style.map { store.allowStyleAgain($0) } }.controlSize(.small) }
            if row.canRemove { Button(L("settings.styles.removeButton"), role: .destructive) { style.map { store.removeStyle($0) } }.controlSize(.small) }
            Spacer(minLength: 0)
        }
    }

    private func rejectionRow(_ rejection: StyleRejection) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(verbatim: rejection.error.code + " " + StyleChrome.separator + " " + rejection.error.message)
                .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Text(verbatim: rejection.path).font(.system(size: 10, design: .monospaced)).foregroundStyle(.tertiary)
                .textSelection(.enabled).lineLimit(1).truncationMode(.middle)
        }
        .padding(.vertical, 2).opacity(0.7)
        .accessibilityElement(children: .combine).accessibilityIdentifier("settings-style-rejected")
    }

    private func register() {
        registerError = nil
        guard let url = store.chooseStyleFile() else { return }
        switch store.readStyleCandidate(at: url) {
        case .candidate(let style, let data): candidate = StyleApprovalCandidate(style: style, data: data)
        case .refused(let message): registerError = message
        }
    }
}
