import MightyCore
import SwiftUI

/// The workspace window's pane-worktree sheets and the new pane's folder choice (``PaneWorktreeChoice``), kept out of
/// `WorkspaceView`'s modifier chain so the compiler type-checks that chain in time.
struct PaneWorktreeModals: ViewModifier {
    @ObservedObject var store: AppStore

    func body(content: Content) -> some View {
        content
            .sheet(item: $store.paneWorktreeChangesTarget) { target in
                PaneWorktreeChangesSheet(target: target) { store.paneWorktreeChangesTarget = nil }
            }
            .sheet(item: listTarget) { target in
                PaneWorktreeListSheet(workspaceId: target.id).environmentObject(store)
            }
            .confirmationDialog(L("paneWorktree.choice.title"), isPresented: choiceShown, titleVisibility: .visible,
                                presenting: store.paneWorktreeChoice) { choice in
                if choice.gitRefusal == nil {
                    Button(L("paneWorktree.choice.worktree")) { store.answerPaneWorktreeChoice(choice, worktree: true) }
                        .keyboardShortcut(.defaultAction)
                    Button(L("paneWorktree.choice.always")) { store.answerPaneWorktreeChoice(choice, worktree: true, always: true) }
                }
                Button(L("paneWorktree.choice.sameFolder")) { store.answerPaneWorktreeChoice(choice, worktree: false) }
                Button(L("common.cancel"), role: .cancel) { store.paneWorktreeChoice = nil }
            } message: { choice in
                Text(verbatim: choice.gitRefusal.map { L("paneWorktree.choice.notGit", ["reason": $0]) } ?? L("paneWorktree.choice.detail"))
            }
    }

    private var listTarget: Binding<PaneWorktreeListTarget?> {
        Binding(get: { store.paneWorktreeListWorkspace.map(PaneWorktreeListTarget.init) }, set: { store.paneWorktreeListWorkspace = $0?.id })
    }

    private var choiceShown: Binding<Bool> {
        Binding(get: { store.paneWorktreeChoice != nil }, set: { if !$0 { store.paneWorktreeChoice = nil } })
    }
}
