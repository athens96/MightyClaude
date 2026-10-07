import AppKit
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
    private let onClose: (CompanionPlanWindow) -> Void

    init(approval: CompanionApproval, store: AppStore, revising: Bool, onClose: @escaping (CompanionPlanWindow) -> Void) {
        self.approval = approval
        self.onClose = onClose
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 560, height: 560), styleMask: [.titled, .closable, .resizable, .miniaturizable], backing: .buffered, defer: false)
        super.init()
        let place = approval.workspaceName.isEmpty ? approval.sessionTitle : approval.workspaceName + " · " + approval.sessionTitle
        window.title = L("plan.card.title") + " · " + place
        window.isReleasedWhenClosed = false
        window.minSize = NSSize(width: 420, height: 360)
        window.contentView = NSHostingView(rootView: PlanApprovalCard(sessionId: approval.sessionId, request: approval.request, inDiagram: true, startsRevising: revising)
            .padding(DesignMetrics.Inset.sheet)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .background(Palette.panel)
            .accessibilityIdentifier("pet-plan-window")
            .environmentObject(store))
        window.delegate = self
        window.center()
    }

    /// Brought forward only from the user's click on the pet's bubble.
    func show() {
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    func close() { window.close() }

    func windowWillClose(_ notification: Notification) {
        window.delegate = nil
        onClose(self)
    }
}
