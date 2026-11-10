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

/// 요청 변경 검토: the files one request changed on the left, the selected
/// file's diff against its content before the request on the right. A click
/// on a diff line opens a note on it; the notes go to the pane's composer as
/// one follow-up request (`ReviewNotes`). The checked files can be put back
/// (`RunRevert`). A panel (kind C).
struct RunChangesSheet: View {
    let target: RunChangesTarget
    let store: FileOriginalStore
    /// Puts the notes' request into the pane's composer.
    var onSendNotes: ((String) -> Void)? = nil
    let onClose: () -> Void

    @ViewState private var items: [RunRevert.Item]?
    @ViewState private var chosen: Set<String> = []
    @ViewState private var confirming = false
    @ViewState private var working = false
    @ViewState private var outcomes: [String: RunRevert.Outcome] = [:]
    /// Counts the reverts done, so the shown diff is read again after one.
    @ViewState private var revision = 0
    @ViewState private var selected: String?
    @ViewState private var changes: FileChangeSet?
    @ViewState private var loadingDiff = false
    @ViewState private var notes: [ReviewNote] = []
    @ViewState private var picked: FileDiffView.Row?
    @ViewState private var noteText = ""
    @FocusState private var noteFocused: Bool

    private var revertible: [RunRevert.Item] { (items ?? []).filter(\.revertible) }
    private var chosenItems: [RunRevert.Item] { revertible.filter { chosen.contains($0.path) } }

    var body: some View {
        DialogScaffold(L("runChanges.title"), subtitle: target.root.path, systemImage: "doc.text.magnifyingglass",
                       close: DialogClose(busy: working, identifier: "run-changes-close", action: onClose)) {
            Group {
                if let items {
                    if items.isEmpty {
                        Text(L("runChanges.empty")).font(.ui(size: 12)).foregroundStyle(Palette.ink2)
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                    } else {
                        HStack(alignment: .top, spacing: DesignMetrics.Spacing.md) {
                            list(items).frame(width: 280)
                            Divider()
                            review.frame(maxWidth: .infinity, maxHeight: .infinity)
                        }
                    }
                } else {
                    DialogLoading(skeletonRows: 3)
                }
            }
            .frame(minHeight: 420, maxHeight: .infinity)
        } footer: {
            DialogFooter {
                Text(summary).foregroundStyle(Palette.ink2)
            } buttons: {
                Button(L("runChanges.revertChosen", ["count": "\(chosenItems.count)"])) { confirming = true }
                    .disabled(chosenItems.isEmpty || working)
                    .accessibilityIdentifier("run-changes-revert")
                if let onSendNotes {
                    Button(L("runChanges.notes.send", ["count": "\(notes.count)"])) {
                        onSendNotes(ReviewNotes.prompt(notes, header: L("runChanges.notes.header"), removedLabel: L("runChanges.notes.removedLine")))
                        onClose()
                    }
                    .buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction)
                    .disabled(notes.isEmpty || working)
                    .accessibilityIdentifier("run-changes-send-notes")
                }
            }
        }
        .dialogSheetWidth(1_000)
        .confirmationDialog(L("runChanges.confirm.title", ["count": "\(chosenItems.count)"]), isPresented: $confirming) {
            Button(L("runChanges.confirm.revert"), role: .destructive) { revert() }
            Button(L("runChanges.confirm.cancel"), role: .cancel) {}
        } message: {
            Text(L("runChanges.confirm.message"))
        }
        .task(id: target) { await reload() }
        .task(id: DiffKey(path: selected, revision: revision)) { await loadDiff() }
    }

    private func list(_ items: [RunRevert.Item]) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            HStack {
                Text(L("runChanges.files", ["count": "\(items.count)"])).font(.ui(size: 11, weight: .semibold)).foregroundStyle(Palette.ink2)
                Spacer()
                Button(chosenItems.count == revertible.count && !revertible.isEmpty ? L("runChanges.chooseNone") : L("runChanges.chooseAll")) {
                    chosen = chosenItems.count == revertible.count ? [] : Set(revertible.map(\.path))
                }
                .buttonStyle(.plain).font(.ui(size: 11)).foregroundStyle(Palette.accent).disabled(revertible.isEmpty || working)
            }
            ScrollView(.vertical) {
                LazyVStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                    ForEach(items) { item in row(item) }
                }
            }
        }
    }

    /// The selected file's diff, the note being written on a picked line, and the notes so far.
    private var review: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            Group {
                if let changes {
                    if changes.unchanged {
                        placeholder(L("runChanges.diff.unchanged"))
                    } else {
                        FileDiffView(changes: changes, onLine: onSendNotes == nil ? nil : { pick($0) },
                                     notedRows: notedRows, pickedRow: picked?.id)
                            .background(Palette.raised.opacity(0.4), in: RoundedRectangle(cornerRadius: 6))
                    }
                } else if loadingDiff {
                    DialogLoading()
                } else {
                    placeholder(selected == nil ? L("runChanges.diff.pick") : L("runChanges.diff.none"))
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            if onSendNotes != nil {
                if let picked, let selected {
                    noteEditor(picked, path: selected)
                } else if changes != nil {
                    Text(L("runChanges.notes.hint")).font(.ui(size: 11)).foregroundStyle(Palette.ink2)
                }
                if !notes.isEmpty { notesList }
            }
        }
    }

    private func placeholder(_ text: String) -> some View {
        Text(text).font(.ui(size: 12)).foregroundStyle(Palette.ink2).frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func noteEditor(_ row: FileDiffView.Row, path: String) -> some View {
        let place = Self.place(row, path: path)
        return VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
            Text(L("runChanges.notes.on", ["place": place])).font(.ui(size: 11, weight: .semibold)).foregroundStyle(Palette.ink2)
            HStack(spacing: DesignMetrics.Spacing.sm) {
                TextField(L("runChanges.notes.placeholder"), text: $noteText, axis: .vertical)
                    .textFieldStyle(.roundedBorder).font(.ui(size: 12)).lineLimit(1...4)
                    .focused($noteFocused)
                    .onSubmit { addNote(row, path: path) }
                    .accessibilityIdentifier("run-changes-note-field")
                Button(L("runChanges.notes.add")) { addNote(row, path: path) }
                    .disabled(noteText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                Button(L("runChanges.confirm.cancel")) { picked = nil; noteText = "" }
            }
        }
    }

    private var notesList: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
            Text(L("runChanges.notes.title", ["count": "\(notes.count)"])).font(.ui(size: 11, weight: .semibold)).foregroundStyle(Palette.ink2)
            ScrollView(.vertical) {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                    ForEach(notes) { note in
                        HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                            Text(note.side == .new ? "\(note.path):\(note.line)" : "\(note.path) −\(note.line)")
                                .font(.ui(size: 10, design: .monospaced)).foregroundStyle(Palette.accent)
                                .onTapGesture { selected = note.path }
                            Text(note.text).font(.ui(size: 11)).foregroundStyle(Palette.ink).lineLimit(2)
                            Spacer(minLength: 0)
                            Button { notes.removeAll { $0.id == note.id } } label: { Image(systemName: "xmark.circle") }
                                .buttonStyle(.plain).foregroundStyle(Palette.ink2)
                                .help(L("runChanges.notes.remove")).accessibilityLabel(L("runChanges.notes.remove"))
                        }
                    }
                }
            }
            .frame(maxHeight: 96)
        }
    }

    /// Rows of the shown diff that carry a note: matched by side and line number.
    private var notedRows: Set<Int> {
        guard let changes, let selected else { return [] }
        let here = notes.filter { $0.path == selected }
        return Set(FileDiffView.rows(changes).filter { row in
            here.contains { note in note.side == .new ? row.new == note.line && row.kind != .removed : row.kind == .removed && row.old == note.line }
        }.map(\.id))
    }

    private static func place(_ row: FileDiffView.Row, path: String) -> String {
        if row.kind == .removed, let old = row.old { return "\(path) −\(old)" }
        return "\(path):\(row.new ?? row.old ?? 0)"
    }

    private func pick(_ row: FileDiffView.Row) {
        picked = row; noteText = ""; noteFocused = true
    }

    private func addNote(_ row: FileDiffView.Row, path: String) {
        let text = noteText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return }
        let removed = row.kind == .removed
        guard let line = removed ? row.old : row.new else { return }
        notes.append(ReviewNote(path: path, line: line, side: removed ? .old : .new, quote: row.text, text: text))
        picked = nil; noteText = ""
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
        .background(selected == item.path ? Palette.accentSoft : Palette.raised, in: RoundedRectangle(cornerRadius: 6))
        .contentShape(Rectangle())
        .onTapGesture { selected = item.path }
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

    private func reload() async {
        let target = target, store = store
        let plan = await Task.detached(priority: .userInitiated) {
            RunRevert.plan(store: store, sessionId: target.sessionID, runId: target.runID, root: target.root)
        }.value
        items = plan
        chosen.formIntersection(plan.filter(\.revertible).map(\.path))
        if selected == nil || !plan.contains(where: { $0.path == selected }) { selected = plan.first?.path }
    }

    private func loadDiff() async {
        guard let path = selected else { changes = nil; return }
        let target = target, store = store
        loadingDiff = true; changes = nil; picked = nil
        let value = await FileChanges.read(store: store, workspaceRoot: target.root, relativePath: path, sessionId: target.sessionID, runId: target.runID)
        guard !Task.isCancelled, selected == path else { return }
        changes = value; loadingDiff = false
    }

    private func revert() {
        let target = target, store = store, paths = chosenItems.map(\.path)
        working = true
        Task {
            let result = await Task.detached(priority: .userInitiated) {
                RunRevert.apply(store: store, sessionId: target.sessionID, runId: target.runID, root: target.root, paths: paths)
            }.value
            outcomes.merge(result) { _, new in new }
            await reload()
            revision += 1
            working = false
        }
    }
}

/// What the review's diff is read again for: another file, or a revert that changed the files.
private struct DiffKey: Hashable {
    let path: String?
    let revision: Int
}
