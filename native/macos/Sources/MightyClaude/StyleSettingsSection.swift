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
                Button("파일에서 스타일 등록…") { register() }.disabled(store.styleTrustLocked)
                    .accessibilityIdentifier("settings-style-register")
                Button("다시 스캔") { store.rescanStyles() }
                    .accessibilityIdentifier("settings-style-rescan")
                Spacer(minLength: 0)
            }
            if let registerError {
                Label(registerError, systemImage: "exclamationmark.triangle").font(.system(size: 11)).foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Text("내장 스타일은 언제나 쓸 수 있습니다. 사용자가 등록한 파일과 저장소에서 발견된 파일은 내용을 한 번 확인한 뒤에만 쓰입니다.")
                .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            ForEach(rows) { row in styleRow(row) }
            ForEach(store.styleRejections, id: \.path) { rejection in rejectionRow(rejection) }
        } header: {
            Text("마이티 스타일")
        }
        .sheet(item: $candidate) { item in
            StyleApprovalSheet(candidate: item, onClose: { candidate = nil }).environmentObject(store)
        }
    }

    private var lockBanner: some View {
        VStack(alignment: .leading, spacing: 3) {
            Label(StyleSettingsList.lockedMessage(store.styleTrustPath), systemImage: "exclamationmark.triangle")
                .font(.system(size: 11, weight: .medium)).foregroundStyle(.orange)
            Text("이 파일을 직접 고치거나 지운 뒤 다시 스캔하세요. 그때까지 허용과 취소는 저장되지 않습니다.")
                .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
        .textSelection(.enabled)
        .accessibilityIdentifier("settings-style-locked")
    }

    private func styleRow(_ row: StyleSettingsRow) -> some View {
        let head = [row.name, row.badge].compactMap { $0 }.joined(separator: " " + StyleChrome.separator + " ")
        let detail = [row.styleId, "해시 " + row.hashPrefix, "행동 \(row.actionCount)", "자동 허용 \(row.autoAllowCount)"]
            .joined(separator: " " + StyleChrome.separator + " ")
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
            Button("내용 보기") {
                guard let style else { return }
                candidate = StyleApprovalCandidate(style: style, data: store.styleBytes(for: style), readOnly: true)
            }.controlSize(.small).disabled(style == nil)
            if row.canApprove {
                Button("허용") {
                    guard let style else { return }
                    candidate = StyleApprovalCandidate(style: style, data: nil)
                }.controlSize(.small)
            }
            if row.canRevoke { Button("취소") { style.map { store.revokeStyle($0) } }.controlSize(.small) }
            // The button drops the refusal; the style then goes back through
            // the approval card like any other pending one (§4.2).
            if row.canAllowAgain { Button("차단 해제") { style.map { store.allowStyleAgain($0) } }.controlSize(.small) }
            if row.canRemove { Button("제거", role: .destructive) { style.map { store.removeStyle($0) } }.controlSize(.small) }
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
