import AppKit
import SwiftUI
import MightyCore

@main
struct MightyClaudeApp: App {
    @NSApplicationDelegateAdaptor(ApplicationDelegate.self) private var delegate
    @StateObject private var store = AppStore.shared

    var body: some Scene {
        WindowGroup("MightyClaude", id: "workspace") {
            WorkspaceView()
                .environmentObject(store)
                .frame(minWidth: 940, minHeight: 650)
                .preferredColorScheme(store.snapshot.theme == "light" ? .light : .dark)
                .tint(Palette.accent)
                .task { await store.load() }
        }
        .defaultSize(width: 1360, height: 900)
        .windowStyle(.hiddenTitleBar)
        .commands {
            CommandGroup(replacing: .newItem) {
                Button("프로젝트 폴더 열기…") { store.openWorkspace() }
                    .keyboardShortcut("o", modifiers: .command)
                    .disabled(store.hasModal)
                Button("새 Claude 실행 창") { store.addSession(kind: "claude") }
                    .keyboardShortcut("n", modifiers: .command)
                    .disabled(store.activeWorkspace == nil || store.hasModal)
            }
            CommandMenu("워크스페이스") {
                Button("워크스페이스 검색") { store.focusSearch = true }
                    .keyboardShortcut("k", modifiers: .command).disabled(store.hasModal)
                Button("터미널 실행 창 추가") { store.addSession(kind: "shell") }
                    .keyboardShortcut("t", modifiers: .command)
                    .disabled(store.activeWorkspace == nil || store.hasModal)
                Divider()
                Button("설정…") { store.showSettings = true }
                    .keyboardShortcut(",", modifiers: .command)
                    .disabled(store.hasModal)
            }
        }
    }
}

@MainActor
final class ApplicationDelegate: NSObject, NSApplicationDelegate {
    private var terminating = false
    private var terminationReady = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        if let image = BrandAssets.icon { NSApp.applicationIconImage = image }
        NSApp.activate(ignoringOtherApps: true)
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        if terminationReady { return .terminateNow }
        if terminating { return .terminateCancel }
        terminating = true
        Task {
            await AppStore.shared.shutdown()
            terminationReady = true
            sender.terminate(nil)
        }
        // Keep the normal event loop active while asynchronous process cleanup and
        // state saving finish. terminateLater enters a modal loop that can block
        // MainActor jobs when termination was requested from a SwiftUI Task.
        return .terminateCancel
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

}

enum BrandAssets {
    static let icon: NSImage? = {
        if let url = Bundle.main.url(forResource: "MightyClaude", withExtension: "icns"),
           let image = NSImage(contentsOf: url) { return image }
        if let url = Bundle.main.url(forResource: "mightyclaude", withExtension: "png"),
           let image = NSImage(contentsOf: url) { return image }
        // Support swift run from the repository root or native/macos as well.
        let directory = URL(fileURLWithPath: FileManager.default.currentDirectoryPath, isDirectory: true)
        for root in [directory, directory.deletingLastPathComponent().deletingLastPathComponent()] {
            if let image = NSImage(contentsOf: root.appendingPathComponent("assets/icons/mightyclaude.png")) { return image }
        }
        return nil
    }()
}
