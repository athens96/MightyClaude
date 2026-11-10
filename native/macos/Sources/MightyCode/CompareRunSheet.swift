import MightyCore
import SwiftUI

/// 비교 결과: the children one compare run started, side by side — agent,
/// state, what each changed against the parent's branch, and what it cost —
/// each with its pane, 병합 and 폐기 (the sidebar card's own actions). A panel (kind C).
struct CompareRunSheet: View {
    @EnvironmentObject private var store: AppStore
    let parentId: String
    let onClose: () -> Void

    @ViewState private var stats: [String: CompareRun.Stat] = [:]

    private var group: [DelegationChildRow] { store.compareGroup(parentId: parentId) }

    var body: some View {
        DialogScaffold(L("compare.title"), subtitle: group.first?.task, systemImage: "square.split.3x1",
                       close: DialogClose(identifier: "compare-close", action: onClose), tools: { EmptyView() }) {
            if group.isEmpty {
                Text(L("compare.empty")).font(.ui(size: 12)).foregroundStyle(Palette.ink2)
                    .frame(maxWidth: .infinity, minHeight: 160)
            } else {
                HStack(alignment: .top, spacing: DesignMetrics.Spacing.md) {
                    ForEach(group) { child in column(child) }
                }
            }
        }
        .dialogSheetWidth(DesignMetrics.Dialog.sheetWidthL)
        .task(id: group.map { "\($0.id)|\($0.state.rawValue)" }) { await readStats() }
    }

    private func column(_ child: DelegationChildRow) -> some View {
        let session = store.snapshot.sessions.first { $0.id == child.id }
        let stat = stats[child.id]
        return VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            HStack(spacing: DesignMetrics.Spacing.xs) {
                if let image = ProviderIconImage.image(provider: child.agent, pointSize: 14) { Image(nsImage: image) }
                Text(ProviderOptions.label(child.agent)).font(.ui(size: 13, weight: .semibold))
            }
            Text(DelegationText.state(child.state)).font(.ui(size: 11)).foregroundStyle(child.state == .reported ? Palette.doneText : Palette.ink2)
            Divider()
            metric(L("compare.files"), stat.map { "\($0.files)" } ?? "–")
            metric(L("compare.lines"), stat.map { "+\($0.insertions) −\($0.deletions)" } ?? "–")
            metric(L("compare.cost"), session?.sessionUsage?.costUSD.map { String(format: "$%.2f", $0) } ?? "–")
            Spacer(minLength: DesignMetrics.Spacing.sm)
            Button(L("compare.openPane")) { store.selectSession(child.id); onClose() }.disabled(session == nil)
            Button(L("delegation.card.merge")) { store.mergeChildFromCard(child.id) }
                .buttonStyle(.borderedProminent).disabled(child.state != .reported)
                .help(child.state == .reported ? "" : L("compare.mergeWhenReported"))
            Button(L("delegation.card.discard"), role: .destructive) { store.askToDiscardChild(child.id) }
        }
        .frame(maxWidth: .infinity, minHeight: 240, alignment: .topLeading)
        .padding(DesignMetrics.Spacing.md)
        .background(Palette.raised, in: RoundedRectangle(cornerRadius: 8))
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("compare-column-\(child.id)")
    }

    private func metric(_ label: String, _ value: String) -> some View {
        HStack {
            Text(label).font(.ui(size: 11)).foregroundStyle(Palette.ink2)
            Spacer()
            Text(value).font(.ui(size: 12, design: .monospaced)).monospacedDigit()
        }
    }

    private func readStats() async {
        for child in group {
            guard let checkout = child.parentCheckout else { continue }
            if let stat = await CompareRun.stat(checkout: checkout, parentBranch: child.parentBranch, childId: child.id) { stats[child.id] = stat }
        }
    }
}
