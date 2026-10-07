import AppKit
import Combine
import MightyCore
import SwiftUI

/// The pet's plan in a small titled window of its own. The pet's panel never
/// takes the keyboard, so a change request is written here, on the pane's own
/// plan card with its Markdown, its change-request check and all four answers.
/// The companion closes it once the request is no longer pending.
@MainActor
final class CompanionPlanWindow: NSObject, NSWindowDelegate {
    let approval: CompanionApproval
    private let window: NSWindow
    private let state = CompanionPlanWindowState()
    private let onClose: (CompanionPlanWindow) -> Void
    private var themeSubscription: AnyCancellable?

    init(approval: CompanionApproval, store: AppStore, revising: Bool, onClose: @escaping (CompanionPlanWindow) -> Void) {
        self.approval = approval
        self.onClose = onClose
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 560, height: 560), styleMask: [.titled, .closable, .resizable, .miniaturizable], backing: .buffered, defer: false)
        super.init()
        let place = approval.workspaceName.isEmpty ? approval.sessionTitle : approval.workspaceName + " · " + approval.sessionTitle
        window.title = L("plan.card.title") + " · " + place
        window.isReleasedWhenClosed = false
        window.minSize = NSSize(width: 420, height: 360)
        window.contentView = NSHostingView(rootView: CompanionPlanWindowRoot(approval: approval, revising: revising, state: state)
            .environmentObject(store))
        // The title bar and every Palette colour read the window's appearance:
        // the app's own theme, as it is now and after each toggle.
        themeSubscription = store.$snapshot.map(\.theme).removeDuplicates().sink { [weak window] theme in
            window?.appearance = NSAppearance(named: theme == "light" ? .aqua : .darkAqua)
        }
        window.delegate = self
        window.center()
    }

    /// Brought forward only from the user's click on the pet's bubble.
    func show() {
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    /// The pet's 수정 요청 again, on a window already open: its change request box opens.
    func startRevising() { state.reviseRequests += 1 }

    func close() { window.close() }

    func windowWillClose(_ notification: Notification) {
        window.delegate = nil
        themeSubscription = nil
        onClose(self)
    }
}

/// What the companion asks of a plan window already on screen.
@MainActor
private final class CompanionPlanWindowState: ObservableObject {
    @Published var reviseRequests = 0
}

/// The window's root: the plan card in the app's theme, following its toggle
/// as the main window does (`MightyClaudeApp`).
private struct CompanionPlanWindowRoot: View {
    @EnvironmentObject private var store: AppStore
    let approval: CompanionApproval
    let revising: Bool
    @ObservedObject var state: CompanionPlanWindowState

    var body: some View {
        // No 펼치기: this window already is the large view, and its sheet would open on the main window.
        PlanApprovalCard(sessionId: approval.sessionId, request: approval.request, inDiagram: true, startsRevising: revising,
                         showsExpand: false, reviseRequests: state.reviseRequests)
            .padding(DesignMetrics.Inset.sheet)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .background(Palette.panel)
            .preferredColorScheme(store.snapshot.theme == "light" ? .light : .dark)
            .accessibilityIdentifier("pet-plan-window")
    }
}
