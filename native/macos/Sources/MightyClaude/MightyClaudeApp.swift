import AppKit
import SwiftUI
import MightyCore

@main
enum MightyClaudeLauncher {
    @MainActor
    static func main() {
        // --verify-resources is handled before CEF, NSApplication, IME registration
        // or any other initialisation so the binary can be called headlessly by the
        // build and install scripts to prove the packaged bundle is correct.
        if CommandLine.arguments.contains("--verify-resources") {
            ResourceVerifier.run()
            // run() exits; this line is never reached.
        }
        // The per-pane MCP server a Claude or Codex run launches: stdio only, no GUI.
        if CommandLine.arguments.contains(PaneMCPServerLocation.headlessArgument) {
            AgentIOMCPCommand.run()
        }
        // Before AppKit reads the language it draws its own menus in.
        AppLanguage.applyToSystemInterface()
        // SwiftUI creates its own AppKitApplication if NSApp does not exist.
        // NSPrincipalClass alone is ignored by App.main(), so establish our
        // CEF-compatible singleton before handing scene management to SwiftUI.
        _ = MightyApplication.shared
        CefBrowserEngine.bootstrapRuntime()
        MightyClaudeApp.main()
    }
}

struct MightyClaudeApp: App {
    @NSApplicationDelegateAdaptor(ApplicationDelegate.self) private var delegate
    @StateObject private var store = AppStore.shared

    var body: some Scene {
        WindowGroup("Mighty Claude", id: "workspace") {
            ZStack {
                if store.isLoaded {
                    WorkspaceView()
                } else {
                    LaunchSplashView()
                }
            }
            .environmentObject(store)
            .frame(minWidth: 940, minHeight: 650)
            .preferredColorScheme(store.snapshot.theme == "light" ? .light : .dark)
            .tint(Palette.accent)
            // Keep the load task on the stable container: replacing the splash
            // must not cancel startup's remaining background setup.
            .task { await store.load() }
        }
        .defaultSize(width: 1360, height: 900)
        .windowStyle(.hiddenTitleBar)
        .commands {
            CommandGroup(replacing: .newItem) {
                Button(L("menu.openProject")) { store.openWorkspace() }
                    .keyboardShortcut("o", modifiers: .command)
                    .disabled(!store.isLoaded || store.hasModal)
                Button(L("menu.newClaudePane")) { store.addSession(kind: "claude") }
                    .keyboardShortcut("n", modifiers: .command)
                    .disabled(!store.isLoaded || store.activeWorkspace == nil || store.hasModal)
            }
            CommandMenu(L("menu.workspace")) {
                Button(L("menu.searchWorkspaces")) { store.focusSearch = true }
                    .keyboardShortcut("k", modifiers: .command).disabled(!store.isLoaded || store.hasModal)
                Button(L("menu.addTerminalPane")) { store.addSession(kind: "shell") }
                    .keyboardShortcut("t", modifiers: .command)
                    .disabled(!store.isLoaded || store.activeWorkspace == nil || store.hasModal)
                Button(L("menu.showFiles")) { store.openFilePane() }
                    .keyboardShortcut("e", modifiers: [.command, .shift])
                    .disabled(!store.isLoaded || store.activeWorkspace == nil || store.hasModal)
                Divider()
                Button(L("menu.reconnectInputMethod")) { store.reconnectInputMethod(editor: nil) }
                Button(L("menu.saveInputDiagnostics")) { store.saveInputMethodDiagnostics() }
                Divider()
                Button(L("menu.settings")) { store.showSettings = true }
                    .keyboardShortcut(",", modifiers: .command)
                    .disabled(!store.isLoaded || store.hasModal)
            }
        }
    }
}

@MainActor
final class ApplicationDelegate: NSObject, NSApplicationDelegate {
    private var terminating = false
    private var terminationReady = false
    private let copyRouter = ApplicationCopyRouter()

    func applicationDidFinishLaunching(_ notification: Notification) {
        copyRouter.install()
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
            finishTermination(sender)
        }
        // A cleanup step that hangs must not keep the app from quitting. Agent
        // runs and terminal processes were signalled first thing and are killed
        // at the deadline; saved state restores a run still marked running as
        // stopped.
        Task {
            try? await Task.sleep(for: .seconds(Self.terminationDeadline))
            finishTermination(sender, deadline: true)
        }
        // Keep the normal event loop active while asynchronous process cleanup and
        // state saving finish. terminateLater enters a modal loop that can block
        // MainActor jobs when termination was requested from a SwiftUI Task.
        return .terminateCancel
    }

    /// Seconds quit waits for cleanup before the app ends regardless.
    static let terminationDeadline: Double = 6

    private func finishTermination(_ sender: NSApplication, deadline: Bool = false) {
        guard !terminationReady else { return }
        terminationReady = true
        // Cleanup hung: nothing it would still have stopped outlives the app.
        if deadline { AppStore.shared.killProcessesAtQuitDeadline() }
        sender.terminate(nil)
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    func applicationWillTerminate(_ notification: Notification) {
        copyRouter.uninstall()
    }

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
