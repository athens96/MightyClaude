import MightyCore
import SwiftUI

/// 저장한 요청 (`/prompts`): the saved requests this pane's workspace offers,
/// each to edit, delete or put in the composer, and a new one from the
/// composer's draft. A panel (kind C).
struct SavedPromptsSheet: View {
    @EnvironmentObject private var store: AppStore
    let sessionID: String
    let onClose: () -> Void

    @ViewState private var editing: SavedPrompt?
    @ViewState private var name = ""
    @ViewState private var text = ""
    @ViewState private var sharedEverywhere = true

    private var session: RunSession? { store.snapshot.sessions.first { $0.id == sessionID } }
    private var offered: [SavedPrompt] { SavedPrompts.offered(store.savedPrompts, workspaceId: session?.workspaceId) }
    private var normalized: String? { SavedPrompts.normalizedName(name) }

    var body: some View {
        DialogScaffold(L("savedPrompts.title"), subtitle: L("savedPrompts.subtitle"), systemImage: "text.badge.star",
                       close: DialogClose(identifier: "saved-prompts-close", action: onClose), tools: { EmptyView() }) {
            HStack(alignment: .top, spacing: DesignMetrics.Spacing.md) {
                list.frame(width: 240)
                Divider()
                editor.frame(maxWidth: .infinity)
            }
            .frame(minHeight: 360)
        }
        .dialogSheetWidth(DesignMetrics.Dialog.sheetWidthL)
        .onAppear { startNew(fromDraft: true) }
    }

    private var list: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            Button { startNew(fromDraft: false) } label: { Label(L("savedPrompts.new"), systemImage: "plus") }
                .buttonStyle(.plain).foregroundStyle(Palette.accent).font(.ui(size: 12))
            if offered.isEmpty {
                Text(L("savedPrompts.empty")).font(.ui(size: 11)).foregroundStyle(Palette.ink2)
            }
            ScrollView(.vertical) {
                LazyVStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                    ForEach(offered) { prompt in
                        VStack(alignment: .leading, spacing: 2) {
                            Text("/" + SavedPrompts.invocationPrefix + prompt.name).font(.code(size: 11)).foregroundStyle(Palette.ink)
                            Text(prompt.workspaceId == nil ? L("savedPrompts.scope.all") : L("savedPrompts.scope.workspace"))
                                .font(.ui(size: 10)).foregroundStyle(Palette.ink2)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(DesignMetrics.Spacing.sm)
                        .background(editing?.id == prompt.id ? Palette.accentSoft : Palette.raised, in: RoundedRectangle(cornerRadius: 6))
                        .contentShape(Rectangle())
                        .onTapGesture { edit(prompt) }
                    }
                }
            }
        }
    }

    private var editor: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            TextField(L("savedPrompts.name"), text: $name).textFieldStyle(.roundedBorder).font(.code(size: 12))
                .accessibilityIdentifier("saved-prompts-name")
            Text(normalized.map { L("savedPrompts.invokeAs", ["command": "/" + SavedPrompts.invocationPrefix + $0]) } ?? L("savedPrompts.nameRule"))
                .font(.ui(size: 10)).foregroundStyle(normalized == nil ? Palette.errText : Palette.ink2)
            TextEditor(text: $text).font(.ui(size: 12)).scrollContentBackground(.hidden)
                .padding(DesignMetrics.Spacing.xs).background(Palette.raised, in: RoundedRectangle(cornerRadius: 6))
                .frame(minHeight: 180)
                .accessibilityIdentifier("saved-prompts-text")
            Text(L("savedPrompts.placeholders")).font(.ui(size: 10)).foregroundStyle(Palette.ink2)
            Toggle(L("savedPrompts.shareAll"), isOn: $sharedEverywhere).toggleStyle(.checkbox).font(.ui(size: 11))
            HStack {
                if let editing, store.savedPrompts.contains(where: { $0.id == editing.id }) {
                    Button(L("savedPrompts.delete"), role: .destructive) {
                        store.saveSavedPrompts(store.savedPrompts.filter { $0.id != editing.id }); startNew(fromDraft: false)
                    }
                    Button(L("savedPrompts.use")) { use(editing) }
                }
                Spacer()
                Button(L("savedPrompts.save")) { save() }
                    .buttonStyle(.borderedProminent)
                    .disabled(normalized == nil || text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || text.utf8.count > SavedPrompts.maximumTextBytes)
                    .accessibilityIdentifier("saved-prompts-save")
            }
        }
    }

    private func startNew(fromDraft: Bool) {
        editing = nil; name = ""; sharedEverywhere = true
        text = fromDraft ? (store.drafts[sessionID] ?? "").trimmingCharacters(in: .whitespacesAndNewlines) : ""
        if text.hasPrefix("/prompts") { text = "" }
    }

    private func edit(_ prompt: SavedPrompt) {
        editing = prompt; name = prompt.name; text = prompt.text; sharedEverywhere = prompt.workspaceId == nil
    }

    private func save() {
        guard let normalized else { return }
        let workspaceId = sharedEverywhere ? nil : session?.workspaceId
        var prompts = store.savedPrompts
        // Saving under a name the same scope already has replaces that one.
        prompts.removeAll { $0.id == editing?.id || ($0.name == normalized && $0.workspaceId == workspaceId) }
        let prompt = SavedPrompt(id: editing?.id ?? UUID().uuidString, name: normalized, text: text, workspaceId: workspaceId)
        prompts.append(prompt)
        if store.saveSavedPrompts(prompts) { edit(prompt) }
    }

    private func use(_ prompt: SavedPrompt) {
        let id = sessionID
        Task {
            guard let text = await store.savedPromptText(prompt.id, sessionID: id) else { return }
            store.drafts[id] = ReviewNotes.appending(text, to: store.drafts[id] ?? "")
            onClose()
        }
    }
}
