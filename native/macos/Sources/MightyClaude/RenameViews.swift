import MightyCore
import SwiftUI

struct RenameTarget: Identifiable {
    enum Kind: String { case workspace, session }
    let kind: Kind
    let targetID: String
    let initialName: String
    var id: String { kind.rawValue + ":" + targetID }
}

struct RenameSheet: View {
    @EnvironmentObject private var store: AppStore
    let target: RenameTarget
    @ViewState private var name: String
    @ViewState private var saveError: String?
    @FocusState private var nameFocused: Bool

    init(target: RenameTarget) {
        self.target = target
        _name = ViewState(initialValue: target.initialName)
    }

    private var heading: String { target.kind == .workspace ? L("pane.rename.workspaceTitle") : L("pane.rename.paneTitle") }
    private var trimmedName: String { name.trimmingCharacters(in: .whitespacesAndNewlines) }
    private var hasControlCharacters: Bool { trimmedName.unicodeScalars.contains { $0.properties.generalCategory == .control } }
    private var validName: Bool { !trimmedName.isEmpty && trimmedName.count <= 120 && !hasControlCharacters }

    var body: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.lg) {
            Text(heading).font(.headline)
            TextField(L("pane.rename.namePlaceholder"), text: $name)
                .textFieldStyle(.roundedBorder)
                .focused($nameFocused)
                .onSubmit { save() }
                .accessibilityIdentifier("rename-name-field")
            Text(target.kind == .workspace ? L("pane.rename.workspaceNote") : L("pane.rename.paneNote"))
                .font(.caption).foregroundStyle(.secondary)
            if trimmedName.count > 120 {
                Text(L("pane.rename.tooLong")).font(.caption).foregroundStyle(Palette.errText)
            }
            if hasControlCharacters {
                Text(L("pane.rename.noNewlines")).font(.caption).foregroundStyle(Palette.errText)
            }
            if let saveError { Text(saveError).font(.caption).foregroundStyle(Palette.errText) }
            HStack {
                if target.kind == .session {
                    Button(L("pane.rename.automatic")) { setAutomatic() }
                        .accessibilityIdentifier("rename-automatic")
                }
                Spacer()
                Button(L("resume.cancel")) { store.renameTarget = nil }.keyboardShortcut(.cancelAction)
                Button(L("pane.rename.save")) { save() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!validName)
                    .accessibilityIdentifier("rename-save")
            }
        }
        .padding(DesignMetrics.Inset.sheet).frame(width: 360)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("rename-sheet")
        .onAppear { nameFocused = true }
    }

    private func save() {
        guard validName, store.renameTarget?.id == target.id else { return }
        let saved = target.kind == .workspace
            ? store.renameWorkspace(target.targetID, to: trimmedName)
            : store.renameSession(target.targetID, to: trimmedName)
        if saved { store.renameTarget = nil }
        else { saveError = L("pane.rename.targetMissing") }
    }

    private func setAutomatic() {
        guard store.renameTarget?.id == target.id else { return }
        store.setSessionAutoTitle(target.targetID)
        store.renameTarget = nil
    }
}
