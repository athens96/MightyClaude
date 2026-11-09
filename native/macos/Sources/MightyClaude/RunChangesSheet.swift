import AppKit
import MightyCore
import SwiftUI

/// What the sheet is about: one request of a pane and the folder its runs worked in.
struct RunChangesTarget: Identifiable, Hashable {
    var id: String { sessionID + "\u{1F}" + runID }
    let sessionID: String
    /// The run as the originals store keys it (`FileOriginalStore.originalsRun`).
    let runID: String
    let root: URL
}

/// 요청 되돌리기: the files one request changed, each with what reverting it
/// would do, and the checked ones put back (`RunRevert`). A panel (kind C).
struct RunChangesSheet: View {
    let target: RunChangesTarget
    let store: FileOriginalStore
    let onClose: () -> Void

    @ViewState private var items: [RunRevert.Item]?
    @ViewState private var chosen: Set<String> = []
    @ViewState private var confirming = false
    @ViewState private var working = false
    @ViewState private var outcomes: [String: RunRevert.Outcome] = [:]

    private var revertible: [RunRevert.Item] { (items ?? []).filter(\.revertible) }
    private var chosenItems: [RunRevert.Item] { revertible.filter { chosen.contains($0.path) } }

    var body: some View {
        DialogScaffold(L("runChanges.title"), subtitle: target.root.path, systemImage: "arrow.uturn.backward.circle",
                       close: DialogClose(busy: working, identifier: "run-changes-close", action: onClose)) {
            Group {
                if let items {
                    if items.isEmpty {
                        Text(L("runChanges.empty")).font(.ui(size: 12)).foregroundStyle(Palette.ink2)
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                    } else {
                        list(items)
                    }
                } else {
                    DialogLoading(skeletonRows: 3)
                }
            }
            .frame(minHeight: 240, maxHeight: .infinity)
        } footer: {
            DialogFooter {
                Text(summary).foregroundStyle(Palette.ink2)
            } buttons: {
                Button(L("runChanges.revertChosen", ["count": "\(chosenItems.count)"])) { confirming = true }
                    .buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction)
                    .disabled(chosenItems.isEmpty || working)
                    .accessibilityIdentifier("run-changes-revert")
            }
        }
        .dialogSheetWidth(DesignMetrics.Dialog.sheetWidthL)
        .confirmationDialog(L("runChanges.confirm.title", ["count": "\(chosenItems.count)"]), isPresented: $confirming) {
            Button(L("runChanges.confirm.revert"), role: .destructive) { revert() }
            Button(L("runChanges.confirm.cancel"), role: .cancel) {}
        } message: {
            Text(L("runChanges.confirm.message"))
        }
        .task(id: target) { await reload(selectAll: true) }
    }

    private func list(_ items: [RunRevert.Item]) -> some View {
        ScrollView(.vertical) {
            LazyVStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                ForEach(items) { item in row(item) }
            }
        }
    }

    private func row(_ item: RunRevert.Item) -> some View {
        HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
            Toggle("", isOn: Binding(get: { chosen.contains(item.path) },
                                     set: { if $0 { chosen.insert(item.path) } else { chosen.remove(item.path) } }))
                .toggleStyle(.checkbox).labelsHidden().disabled(!item.revertible || working)
                .accessibilityLabel(item.path)
            Image(systemName: item.action == .trash ? "doc.badge.plus" : "doc.text")
                .font(.ui(size: 12)).foregroundStyle(item.revertible ? Palette.accent : Palette.ink2)
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                Text(item.path).font(.ui(size: 12, design: .monospaced)).lineLimit(1).truncationMode(.middle).help(item.path)
                Text(detail(item)).font(.ui(size: 10)).foregroundStyle(item.revertible ? Palette.ink2 : Palette.waitText)
            }
            Spacer(minLength: DesignMetrics.Spacing.sm)
            if item.restored > 0 || item.removed > 0 {
                Text("+\(item.restored) −\(item.removed)").font(.ui(size: 10, design: .monospaced)).foregroundStyle(Palette.ink2)
            }
        }
        .padding(DesignMetrics.Spacing.sm)
        .background(Palette.raised, in: RoundedRectangle(cornerRadius: 6))
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("run-changes-row-\(item.path)")
    }

    private func detail(_ item: RunRevert.Item) -> String {
        switch outcomes[item.path] {
        case .reverted?: return L("runChanges.outcome.reverted")
        case .failed(let error)?: return L("runChanges.outcome.failed", ["error": error])
        default: break
        }
        switch item.refusal {
        case .laterRun?: return L("runChanges.refusal.laterRun")
        case .missing?: return L("runChanges.refusal.missing")
        case .unreadable?: return L("runChanges.refusal.unreadable")
        case .unchanged?: return L("runChanges.refusal.unchanged")
        case nil: break
        }
        return item.action == .trash ? L("runChanges.action.trash") : L("runChanges.action.restore")
    }

    private var summary: String {
        guard let items else { return "" }
        let done = outcomes.values.filter { $0 == .reverted }.count
        if done > 0 { return L("runChanges.summary.done", ["count": "\(done)"]) }
        return L("runChanges.summary", ["count": "\(items.count)", "revertible": "\(revertible.count)"])
    }

    private func reload(selectAll: Bool) async {
        let target = target, store = store
        let plan = await Task.detached(priority: .userInitiated) {
            RunRevert.plan(store: store, sessionId: target.sessionID, runId: target.runID, root: target.root)
        }.value
        items = plan
        if selectAll { chosen = Set(plan.filter(\.revertible).map(\.path)) } else { chosen.formIntersection(plan.filter(\.revertible).map(\.path)) }
    }

    private func revert() {
        let target = target, store = store, paths = chosenItems.map(\.path)
        working = true
        Task {
            let result = await Task.detached(priority: .userInitiated) {
                RunRevert.apply(store: store, sessionId: target.sessionID, runId: target.runID, root: target.root, paths: paths)
            }.value
            outcomes.merge(result) { _, new in new }
            await reload(selectAll: false)
            working = false
        }
    }
}
