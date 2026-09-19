import SwiftUI
import MightyCore

/// One style waiting to be read. The bytes travel with it: the card, the hash
/// and the copy it makes are all the same single read (§4.4).
struct StyleApprovalCandidate: Identifiable, Equatable {
    var style: RegisteredStyle
    /// Present for a file the user picked, which is copied on approval.
    var data: Data?
    /// Read-only: the card is being reviewed, not decided.
    var readOnly = false
    var id: String { style.path + "|" + style.hash }

    static func == (left: StyleApprovalCandidate, right: StyleApprovalCandidate) -> Bool { left.id == right.id && left.readOnly == right.readOnly }
}

/// Everything the manifest says, in risk order, before it is ever used.
struct StyleApprovalSheet: View {
    @EnvironmentObject private var store: AppStore
    let candidate: StyleApprovalCandidate
    /// Approving may also change the pane that opened the sheet.
    var onApproved: (RegisteredStyle) -> Void = { _ in }
    let onClose: () -> Void

    @ViewState private var expanded = true
    @ViewState private var confirming = false
    /// `[허용]` stays off until the auto-allow block has actually been drawn:
    /// it is the one block that never folds and it opens at the top (§4.4).
    @ViewState private var autoAllowSeen = false

    private var style: RegisteredStyle { candidate.style }
    private var sections: [StyleApprovalSection] { StyleApprovalCard.sections(style) }
    private var autoAllowCount: Int { StyleApprovalCard.autoAllowCount(style) }
    private var rawText: String {
        guard let data = candidate.data ?? store.styleBytes(for: style) else { return "" }
        return String(decoding: data, as: UTF8.self)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    ForEach(sections) { section in sectionView(section) }
                    rawSection
                }
                .padding(16)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            Divider()
            footer
        }
        .frame(width: 640, height: 560)
        .accessibilityIdentifier("style-approval-sheet")
    }

    private var header: some View {
        HStack(spacing: 8) {
            Text(verbatim: style.manifest.name).font(.system(size: 15, weight: .semibold))
            if let badge = StyleChrome.sourceBadge(style.source) { SourceBadge(text: badge) }
            Spacer(minLength: 0)
            Toggle("전부 펼치기", isOn: $expanded).toggleStyle(.switch).controlSize(.mini)
        }
        .padding(.horizontal, 16).padding(.vertical, 12)
    }

    @ViewBuilder private func sectionView(_ section: StyleApprovalSection) -> some View {
        let hidden = section.foldable && !expanded
        VStack(alignment: .leading, spacing: 5) {
            Text(verbatim: section.title).font(.system(size: 12, weight: .semibold))
            if hidden {
                Text("\(section.lines.count)줄 " + StyleChrome.separator + " 전부 펼치기로 볼 수 있습니다")
                    .font(.system(size: 11)).foregroundStyle(.tertiary)
            } else {
                ForEach(Array(section.lines.enumerated()), id: \.offset) { _, line in
                    lineView(line, monospaced: section.monospaced)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .onAppear { if section.id == "autoAllow" { autoAllowSeen = true } }
        .accessibilityIdentifier("style-approval-\(section.id)")
    }

    /// A manifest string is data: plain text, never markdown (§4.6).
    private func lineView(_ line: String, monospaced: Bool) -> some View {
        Text(verbatim: line)
            .font(.system(size: 11, design: monospaced ? .monospaced : .default))
            .foregroundStyle(.secondary)
            .textSelection(.enabled)
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var rawSection: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text("원본 JSON").font(.system(size: 12, weight: .semibold))
            if expanded {
                Text(verbatim: rawText)
                    .font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary)
                    .textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
            } else {
                Text("전부 펼치기로 볼 수 있습니다").font(.system(size: 11)).foregroundStyle(.tertiary)
            }
        }
        .accessibilityIdentifier("style-approval-raw")
    }

    private var footer: some View {
        HStack(spacing: 8) {
            if confirming {
                Text(verbatim: StyleApprovalCard.secondConfirmation(count: autoAllowCount))
                    .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            } else if store.styleTrustLocked {
                Text(verbatim: StyleSettingsList.lockedMessage(store.styleTrustPath))
                    .font(.system(size: 11)).foregroundStyle(.orange).fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
            Button(candidate.readOnly ? "닫기" : "지금은 안 함", action: onClose)
                .keyboardShortcut(.cancelAction)
                .accessibilityIdentifier("style-approval-dismiss")
            if !candidate.readOnly {
                // The auto-allow block has to have been on screen, and a
                // non-empty list is confirmed a second time (§4.4).
                Button(confirming ? "정말 허용" : "허용") { allow() }
                    .buttonStyle(.borderedProminent)
                    .disabled(store.styleTrustLocked || !autoAllowSeen)
                    .accessibilityIdentifier("style-approval-allow")
            }
        }
        .padding(.horizontal, 16).padding(.vertical, 12)
    }

    private func allow() {
        if autoAllowCount > 0, !confirming { confirming = true; return }
        let approved = style
        store.approveStyle(approved, data: candidate.data) { ok in
            if ok { onApproved(approved) }
        }
        onClose()
    }
}
