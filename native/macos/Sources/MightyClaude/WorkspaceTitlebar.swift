import AppKit
import SwiftUI

/// Installed only over non-editable title/blank space. Native buttons and the
/// selectable path stay outside this region and keep their normal mouse input.
struct WorkspaceTitlebarRegion: NSViewRepresentable {
    var enabled: Bool
    var rename: (() -> Void)?

    func makeNSView(context: Context) -> WorkspaceTitlebarView { WorkspaceTitlebarView() }
    func updateNSView(_ view: WorkspaceTitlebarView, context: Context) {
        view.enabled = enabled; view.rename = rename
        view.toolTip = "끌어서 창 이동 · 두 번 클릭해 확대/복원"
        view.setAccessibilityIdentifier("workspace-titlebar-drag")
        view.setAccessibilityElement(false)
    }
}

final class WorkspaceTitlebarView: NSView {
    var enabled = true
    var rename: (() -> Void)?
    override var mouseDownCanMoveWindow: Bool { false }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseDown(with event: NSEvent) {
        guard enabled, let window else { return }
        if event.clickCount == 2 { window.performZoom(nil) }
        else if event.clickCount == 1 { window.performDrag(with: event) }
    }
    override func menu(for event: NSEvent) -> NSMenu? {
        guard enabled, rename != nil else { return nil }
        let menu = NSMenu()
        let item = NSMenuItem(title: "워크스페이스 이름 변경…", action: #selector(renameWorkspace), keyEquivalent: "")
        item.target = self; menu.addItem(item)
        return menu
    }
    @objc private func renameWorkspace() { if enabled { rename?() } }
}
