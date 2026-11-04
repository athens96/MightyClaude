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
    /// `[Allow]` stays off until the auto-allow block has actually been drawn:
    /// it is the one block that never folds and it opens at the top (§4.4).
    @ViewState private var autoAllowSeen = false

    private var style: RegisteredStyle { candidate.style }
    private var sections: [StyleApprovalSection] { StyleApprovalCard.sections(style) }
    private var autoAllowCount: Int { StyleApprovalCard.autoAllowCount(style) }
    private var rawText: String {
        guard let data = candidate.data ?? store.styleBytes(for: style) else { return "" }
        return String(decoding: data, as: UTF8.self)
    }

    /// A panel (kind C): the source badge and 모두 펼치기 beside the ✕, the manifest in the body, and
    /// [나중에] [허용] at the bottom; a read-only review has a footer only for its note.
    var body: some View {
        DialogScaffold(style.manifest.name, systemImage: "paintpalette",
                       close: DialogClose(identifier: "style-approval-close", action: onClose), scrolls: true) {
            if let badge = StyleChrome.sourceBadge(style.source) { SourceBadge(text: badge) }
            Toggle(L("styles.approval.expandAll"), isOn: $expanded).toggleStyle(.checkbox)
        } content: {
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.lg) {
                ForEach(sections) { section in sectionView(section) }
                rawSection
            }
        } footer: {
            if !candidate.readOnly || confirming || store.styleTrustLocked { footer }
        }
        .dialogPanelFrame(id: "styleApproval", ideal: CGSize(width: 640, height: 560), minimum: CGSize(width: 520, height: 400))
        .accessibilityIdentifier("style-approval-sheet")
    }

    @ViewBuilder private func sectionView(_ section: StyleApprovalSection) -> some View {
        let hidden = section.foldable && !expanded
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
            Text(verbatim: section.title).font(.ui(size: 12, weight: .semibold))
            if hidden {
                Text(L("styles.approval.collapsedLines", ["count": "\(section.lines.count)", "separator": StyleChrome.separator]))
                    .font(.ui(size: 11)).foregroundStyle(.tertiary)
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
            .font(.ui(size: 11, design: monospaced ? .monospaced : .default))
            .foregroundStyle(.secondary)
            .textSelection(.enabled)
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var rawSection: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
            Text(L("styles.approval.raw")).font(.ui(size: 12, weight: .semibold))
            if expanded {
                Text(verbatim: rawText)
                    .font(.ui(size: 10, design: .monospaced)).foregroundStyle(.secondary)
                    .textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
            } else {
                Text(L("styles.approval.collapsedHint")).font(.ui(size: 11)).foregroundStyle(.tertiary)
            }
        }
        .accessibilityIdentifier("style-approval-raw")
    }

    private var footer: some View {
        DialogFooter {
            if confirming {
                Text(verbatim: StyleApprovalCard.secondConfirmation(count: autoAllowCount)).foregroundStyle(.secondary)
            } else if store.styleTrustLocked {
                Text(verbatim: StyleSettingsList.lockedMessage(store.styleTrustPath)).foregroundStyle(Palette.waitText)
            }
        } buttons: {
            if !candidate.readOnly {
                // Esc is the header's ✕; this is the same answer as a button.
                Button(L("statusLine.notNow"), action: onClose)
                    .accessibilityIdentifier("style-approval-dismiss")
                // The auto-allow block has to have been on screen, and a
                // non-empty list is confirmed a second time (§4.4). Return does not allow: trust is a click.
                Button(confirming ? L("styles.approval.confirmAgain") : L("settings.styles.allowButton")) { allow() }
                    .buttonStyle(.borderedProminent)
                    .disabled(store.styleTrustLocked || !autoAllowSeen)
                    .accessibilityIdentifier("style-approval-allow")
            }
        }
    }

    private func allow() {
        if autoAllowCount > 0, !confirming { confirming = true; return }
        // `data` is present only for a file the user picked, which is not yet
        // on disk; the style handed back is the one the fresh registry
        // resolved, so the pane binds to the bytes just approved (§4.4, §4.5).
        store.approveStyle(style, data: candidate.data) { approved in onApproved(approved) }
        onClose()
    }
}
