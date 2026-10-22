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
        // The delegation MCP server a Claude run gets while the delegation switch is on: stdio only, no GUI.
        if CommandLine.arguments.contains(DelegationMCPServer.headlessArgument) {
            DelegationMCPCommand.run()
        }
        // Before AppKit reads the language it draws its own menus in. The help capture
        // gets its languages from the launch arguments and leaves the saved ones alone.
        if !CommandLine.arguments.contains(HelpCapture.argument) { AppLanguage.applyToSystemInterface() }
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
            // Text that names no font of its own is the body size at the user's text size; at
            // the standard size in the system font nothing is set, so small controls keep their own size.
            .font(store.textAppearance.scale == 1 && store.textAppearance.family == nil ? nil : .ui(size: 13))
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
            // View menu: the standard "Toggle Sidebar" place and key.
            CommandGroup(replacing: .sidebar) {
                Button(L(store.sidebarCollapsed ? "sidebar.expand" : "sidebar.collapse")) {
                    withAnimation(.easeInOut(duration: 0.2)) { store.toggleSidebar() }
                }
                .keyboardShortcut("s", modifiers: [.control, .command])
                .disabled(!store.isLoaded || store.hasModal)
            }
            // View menu: the text size of the whole app, terminals included.
            CommandGroup(after: .sidebar) {
                Divider()
                Button(L("menu.textBigger")) { store.setTextAppearance(store.textAppearance.stepped(1)) }
                    .keyboardShortcut("=", modifiers: .command)
                    .disabled(!store.textAppearance.canGrow)
                Button(L("menu.textSmaller")) { store.setTextAppearance(store.textAppearance.stepped(-1)) }
                    .keyboardShortcut("-", modifiers: .command)
                    .disabled(!store.textAppearance.canShrink)
                Button(L("menu.textActualSize")) { store.setTextAppearance(store.textAppearance.with(scale: 1)) }
                    .keyboardShortcut("0", modifiers: .command)
                    .disabled(store.textAppearance.scale == 1)
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
            // Help menu: the user guide in the app's language, on the standard ⌘?.
            CommandGroup(replacing: .help) {
                Button(L("menu.help")) { NSWorkspace.shared.open(HelpSite.current) }
                    .keyboardShortcut("?", modifiers: .command)
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
        // Logout, restart and shutdown send their quit with one of their
        // reasons: a staged update then waits for the next quit. Read from this
        // quit alone, so a logout cancelled earlier leaves nothing behind.
        let reason = NSAppleEventManager.shared().currentAppleEvent?.attributeDescriptor(forKeyword: AEKeyword(kAEQuitReason))?.enumCodeValue
        AppStore.shared.appUpdatePoweringOff = AppUpdateAuto.quitReasonPowersOff(reason)
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
    static let terminationDeadline: Double = LiveRunRegistry.quitDeadline

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
