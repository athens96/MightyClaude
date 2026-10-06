import AppKit
import Darwin
import GhosttyTerminal
import ObjectiveC
import MightyCore
import SwiftUI

/// `--help-capture --profile <dir> -language <ko|en|zh|ja> [--smoke-exit]`: loads the
/// demo profile bundled with MightyCore, seeds it into the empty profile, opens every
/// screen docs/help/shots.json lists and saves each in each theme as
/// `<profile>/help-shots/<lang>/<id>-<theme>.png`, beside a manifest.json that records
/// every shot's status. A shot that fails is recorded and makes the run exit non-zero;
/// nothing is skipped silently.
///
/// The demo panes come from the profile's canned CLI lines replayed through the real
/// parser (`HelpDemoReplay`); no CLI is started and nothing goes on the network. The
/// shell pane is a real terminal whose one command prints the profile's canned output.
/// CI runs this once per language, in a fresh profile each time; never on a
/// developer's Mac, where a second app instance breaks Korean input.
extension AppStore {
    var helpCapturing: Bool { ProcessInfo.processInfo.arguments.contains(HelpCapture.argument) }
}

@MainActor
enum HelpCapture {
    nonisolated static let argument = "--help-capture"

    private struct Shot {
        var entry: [String: Any]
        var failed: Bool
    }

    /// What one screen shows: the window to capture and how to put things back.
    private struct Target {
        let window: NSWindow
        var cleanup: () async -> Void = {}
    }

    /// The seeded demo: pane ids by role, and how each workspace's panes were laid out.
    private final class Demo {
        let profile: DemoProfile
        var panes: [String: String] = [:]
        var modes: [String: String] = [:]
        var terminalProblem: String?
        init(profile: DemoProfile) { self.profile = profile }
        func pane(_ role: String) throws -> String {
            guard let id = panes[role] else { throw MightyError("the demo has no \(role) pane") }
            return id
        }
    }

    static func run(store: AppStore) async {
        let arguments = ProcessInfo.processInfo.arguments
        let language = UserDefaults.standard.string(forKey: "language").flatMap(AppLanguage.init(rawValue:))
        let code = language.map(\.rawValue) ?? "unknown"
        let output = store.dataDirectory.appendingPathComponent("help-shots/\(code)", isDirectory: true)
        var manifest: [String: Any] = ["language": code, "appVersion": store.appVersion]
        var shots: [Shot] = []
        let previousSettingsPane = UserDefaults.standard.object(forKey: "settingsPane")
        do {
            guard arguments.contains("--profile") else { throw MightyError("--help-capture needs a temporary --profile") }
            guard let language, language != .system else { throw MightyError("--help-capture needs -language ko|en|zh|ja") }
            try? FileManager.default.removeItem(at: output)
            try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
            let list = try HelpShotList.decode(read(HelpCaptureResources.shots))
            let profile = try DemoProfile.load(read(HelpCaptureResources.demoProfile), language: language.rawValue)
            manifest["shotCount"] = list.shots.count
            store.runtime = runtime(profile, store: store)
            let main = try await mainWindow(store: store)
            manifest["screen"] = screenInfo(main)
            // The empty profile's first window comes before anything is seeded.
            let ordered = list.shots.filter { $0.screen == .welcome } + list.shots.filter { $0.screen != .welcome }
            var demo: Demo?
            var seedError: String?
            for shot in ordered {
                if shot.screen != .welcome, demo == nil, seedError == nil {
                    let seeded = Demo(profile: profile)
                    do { try await seed(seeded, store: store, main: main); demo = seeded }
                    catch { seedError = error.localizedDescription; manifest["error"] = "seeding the demo profile failed: " + error.localizedDescription }
                }
                if shot.screen != .welcome, let seedError {
                    // Every shot is listed, the ones the failed seeding left out too.
                    shots += shot.themes.map { theme in
                        Shot(entry: ["id": shot.id, "section": shot.section, "screen": shot.screen.rawValue, "theme": theme, "file": "\(shot.id)-\(theme).png",
                                     "status": "failed", "error": "seeding the demo profile failed: " + seedError], failed: true)
                    }
                    continue
                }
                shots += await capture(shot, demo: demo, store: store, main: main, into: output, language: language.rawValue)
            }
        } catch {
            manifest["error"] = error.localizedDescription
        }
        NSApp.appearance = nil
        if let previousSettingsPane { UserDefaults.standard.set(previousSettingsPane, forKey: "settingsPane") }
        else { UserDefaults.standard.removeObject(forKey: "settingsPane") }
        let failed = shots.filter(\.failed).count
        manifest["shots"] = shots.map(\.entry)
        manifest["failed"] = failed
        manifest["passed"] = manifest["error"] == nil && failed == 0 && !shots.isEmpty
        do {
            try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
            try JSONSerialization.data(withJSONObject: manifest, options: [.prettyPrinted, .sortedKeys])
                .write(to: output.appendingPathComponent("manifest.json"), options: .atomic)
        } catch { NSLog("Help capture manifest: %@", error.localizedDescription) }
        if arguments.contains("--smoke-exit") {
            await store.shutdown()
            Darwin.exit(manifest["passed"] as? Bool == true ? 0 : 1)
        }
    }

    // MARK: Files and runtime

    private static func read(_ name: String) throws -> Data {
        let result = ResourceHealthChecker.checkHelpFile(name)
        guard let path = result.resolvedPath else { throw MightyError(result.warning ?? "missing Help/\(name)") }
        return try Data(contentsOf: URL(fileURLWithPath: path))
    }

    /// The CLIs as installed and signed in, without asking a real one.
    private static func runtime(_ profile: DemoProfile, store: AppStore) -> RuntimeInfo {
        let providers = ProviderOptions.ids.map { id -> ProviderRuntime in
            var value = ProviderOptions.fallbackRuntime(id)
            guard let spec = profile.providers.first(where: { $0.id == id }) else { return value }
            value.available = true
            value.version = spec.version
            value.detail = L("provider.available")
            value.capabilities.permissionModes = ProviderOptions.permissionModes(provider: id)
            return value
        }
        let claude = providers.first { $0.id == "claude" }
        return RuntimeInfo(appVersion: store.appVersion, claudeAvailable: claude?.available == true, claudeVersion: claude?.version,
                           modelCatalog: claude?.modelCatalog, providers: providers,
                           mods: ModsRuntime(status: "available", detail: claude?.detail ?? ""))
    }

    private static func mainWindow(store: AppStore) async throws -> NSWindow {
        try await wait("the main window", store: store, timeout: 20) { store.isLoaded && findMainWindow() != nil }
        guard let window = findMainWindow() else { throw MightyError("the main window did not open") }
        return window
    }

    private static func findMainWindow() -> NSWindow? {
        NSApp.windows.first { !($0 is NSPanel) && $0.isVisible && $0.canBecomeMain && $0.level == .normal && $0.contentView != nil }
    }

    // MARK: Seeding

    private static func seed(_ demo: Demo, store: AppStore, main: NSWindow) async throws {
        let profile = demo.profile
        var paths: [String: String] = [:]
        let root = try workspacesRoot(store)
        for spec in profile.workspaces {
            let folder = root.appendingPathComponent(spec.directory, isDirectory: true)
            if root != store.dataDirectory {
                // Outside the profile only a folder an earlier capture made is replaced.
                let marker = root.appendingPathComponent(".mighty-help-demo")
                if FileManager.default.fileExists(atPath: folder.path), !FileManager.default.fileExists(atPath: marker.path) {
                    throw MightyError("refusing to replace \(folder.path): no earlier help capture made it")
                }
                try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
                try Data("Demo workspaces of Mighty Claude's help capture.\n".utf8).write(to: marker, options: .atomic)
            }
            try? FileManager.default.removeItem(at: folder)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            for file in spec.files {
                let url = folder.appendingPathComponent(file.path)
                try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
                try Data(file.content.utf8).write(to: url, options: .atomic)
            }
            let workspace = try await store.repository.approveWorkspace(Workspace(id: spec.id, name: spec.name, path: folder.path))
            if !store.snapshot.workspaces.contains(where: { $0.id == workspace.id }) { store.snapshot.workspaces.append(workspace) }
            paths[spec.id] = workspace.path
        }
        for pane in profile.panes {
            store.selectWorkspace(pane.workspace)
            guard let id = store.addSession(kind: pane.kind, provider: pane.provider, workspaceId: pane.workspace) else { throw MightyError("could not add the \(pane.role) pane") }
            demo.panes[pane.role] = id
            store.updateSession(id) { session in
                session.title = pane.title
                session.titleMode = "fixed"
                session.model = pane.model
                session.settings = RunSettings(effort: pane.effort, permissionMode: pane.permissionMode)
                session.agentViewMode = pane.view == "mighty" ? "mighty" : nil
            }
            for (index, run) in pane.runs.enumerated() {
                let events = HelpDemoReplay.events(sessionId: id, provider: pane.provider, model: pane.model, permissionMode: pane.permissionMode,
                                                   inputId: "help-\(pane.role)-\(index)", input: run.input, frames: run.frames, finish: run.finish)
                store.companion.recordInput(sessionID: id, text: run.input)
                store.companion.beginRun(sessionID: id)
                for event in events { store.apply(event) }
                if let seconds = run.elapsedSeconds {
                    let now = Date()
                    store.updateSession(id) { $0.runTiming = AgentRunTiming(startedAt: now.addingTimeInterval(-seconds), lastObservedAt: now, finishedAt: run.finish == nil ? nil : now) }
                }
            }
            if let draft = pane.draft { store.drafts[id] = draft }
        }
        for provider in profile.providers {
            store.cliAccounts[provider.id] = CLIAccountStatus(provider: provider.id, installed: true, loggedIn: provider.account != nil, account: provider.account, plan: provider.plan)
        }
        for layout in profile.layouts {
            store.selectWorkspace(layout.workspace)
            store.setPaneLayoutPreset(layout.preset)
            store.selectSession(try demo.pane(layout.active))
            demo.modes[layout.workspace] = store.paneLayoutMode(layout.workspace)
        }
        try writeResumeRecords(profile, paths: paths, store: store)
        try await select(profile.activeWorkspace, demo: demo, store: store)
        main.makeKeyAndOrderFront(nil)
        try await Task.sleep(for: .milliseconds(800))
        do { try await prepareTerminal(demo, store: store, prompt: profile.workspaces.first { $0.id == profile.pane(profile.terminal.role)?.workspace }?.name ?? "") }
        catch { demo.terminalProblem = error.localizedDescription }
    }

    /// Where the demo workspace folders go: the profile, or the folder given with
    /// `--help-workspaces <dir>`. CI passes a folder in its throwaway home, so the
    /// pictures show a path like a person's own rather than the runner's temp folder.
    private static func workspacesRoot(_ store: AppStore) throws -> URL {
        let arguments = ProcessInfo.processInfo.arguments
        guard let index = arguments.firstIndex(of: "--help-workspaces") else { return store.dataDirectory }
        guard arguments.indices.contains(index + 1), arguments[index + 1].hasPrefix("/") else { throw MightyError("--help-workspaces needs an absolute folder") }
        return URL(fileURLWithPath: arguments[index + 1], isDirectory: true)
    }

    /// Claude session records the "continue a session" list reads, in a config folder
    /// under the profile: this process alone looks there (CLAUDE_CONFIG_DIR).
    private static func writeResumeRecords(_ profile: DemoProfile, paths: [String: String], store: AppStore) throws {
        let config = store.dataDirectory.appendingPathComponent("help-demo/claude-config", isDirectory: true)
        let now = Date()
        for record in profile.resumeSessions where record.provider == "claude" {
            guard let path = paths[record.workspace] else { throw MightyError("a resume record names an unknown workspace") }
            let folder = HelpDemoRecords.claudeFolder(config: config, workspacePath: path)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let file = folder.appendingPathComponent(record.sessionId + ".jsonl")
            try Data(HelpDemoRecords.claudeLines(record, workspacePath: path, now: now).utf8).write(to: file, options: .atomic)
            try FileManager.default.setAttributes([.modificationDate: now.addingTimeInterval(-record.minutesAgo * 60)], ofItemAtPath: file.path)
        }
        setenv("CLAUDE_CONFIG_DIR", config.path, 1)
    }

    /// Types the demo command into the shell pane's real terminal. The command's name
    /// is a shell function that prints the profile's canned output, so nothing runs.
    private static func prepareTerminal(_ demo: Demo, store: AppStore, prompt: String) async throws {
        let id = try demo.pane(demo.profile.terminal.role)
        try await wait("the terminal to start", store: store, timeout: 20) { store.localTerminals[id]?.ready == true }
        guard let terminal = store.localTerminals[id] else { throw MightyError("the terminal did not start") }
        let spec = demo.profile.terminal
        let file = store.dataDirectory.appendingPathComponent("help-demo/terminal-output.txt")
        try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data((spec.output.joined(separator: "\n") + "\n").utf8).write(to: file, options: .atomic)
        guard let name = spec.command.split(separator: " ").first.map(String.init),
              name.range(of: "^[A-Za-z][A-Za-z0-9_-]*$", options: .regularExpression) != nil else { throw MightyError("the terminal command needs a plain program name") }
        // bash reads PS1; in zsh PS1 and PROMPT are one variable, so PROMPT (set last) wins.
        let setup = "PS1=\(quote(prompt + " $ ")); PROMPT=\(quote(prompt + " %# ")); RPROMPT=''; \(name)() { /bin/cat \(quote(file.path)); }; clear"
        guard terminal.view.paste(text: setup), terminal.view.sendKey(.enter, modifiers: []) else { throw MightyError("could not type into the terminal") }
        try await Task.sleep(for: .milliseconds(700))
        guard terminal.view.paste(text: spec.command), terminal.view.sendKey(.enter, modifiers: []) else { throw MightyError("could not type into the terminal") }
        let marker = spec.output.last { !$0.trimmingCharacters(in: .whitespaces).isEmpty }?.trimmingCharacters(in: .whitespaces) ?? ""
        try await wait("the terminal output", store: store, timeout: 10) { terminal.diagnosticText().contains(marker) }
        clearSelection(terminal)
    }

    /// Reading the terminal's text selects all of it; one click in its corner clears
    /// that highlight, as the terminal smoke does. The pasteboard is not touched.
    private static func clearSelection(_ terminal: LocalTerminalSession) {
        guard let window = terminal.view.window else { return }
        let point = terminal.view.convert(NSPoint(x: 4, y: 4), to: nil)
        for type in [NSEvent.EventType.leftMouseDown, .leftMouseUp] {
            guard let event = NSEvent.mouseEvent(with: type, location: point, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                                 windowNumber: window.windowNumber, context: nil, eventNumber: 0, clickCount: 1, pressure: type == .leftMouseDown ? 1 : 0) else { continue }
            if type == .leftMouseDown { terminal.view.mouseDown(with: event) } else { terminal.view.mouseUp(with: event) }
        }
    }

    private static func quote(_ value: String) -> String { "'" + value.replacingOccurrences(of: "'", with: "'\\''") + "'" }

    private static func select(_ workspace: String, demo: Demo, store: AppStore) async throws {
        store.showsDashboard = false
        // A screen that changed a pane's view puts it back, even when it failed half-way.
        for pane in demo.profile.panes {
            guard let id = demo.panes[pane.role], let session = store.snapshot.sessions.first(where: { $0.id == id }) else { continue }
            let mode = pane.view == "mighty" ? "mighty" : nil
            if session.agentViewMode != mode || session.graphViewMode != nil {
                store.updateSession(id) { $0.agentViewMode = mode; $0.graphViewMode = nil }
            }
        }
        for (id, mode) in demo.modes { store.setPaneLayoutMode(mode, workspaceId: id) }
        store.selectWorkspace(workspace)
        if let layout = demo.profile.layouts.first(where: { $0.workspace == workspace }) { store.selectSession(try demo.pane(layout.active)) }
        try await Task.sleep(for: .milliseconds(300))
    }

    // MARK: Shots

    private static func capture(_ shot: HelpShot, demo: Demo?, store: AppStore, main: NSWindow, into output: URL, language: String) async -> [Shot] {
        func entry(_ theme: String) -> [String: Any] {
            ["id": shot.id, "section": shot.section, "screen": shot.screen.rawValue, "theme": theme, "file": "\(shot.id)-\(theme).png"]
        }
        if let size = shot.window {
            resize(main, to: size)
            try? await Task.sleep(for: .milliseconds(300))
        }
        let target: Target
        do { target = try await open(shot.screen, demo: demo, store: store, main: main) }
        catch {
            await reset(demo: demo, store: store, main: main)
            return shot.themes.map { theme in
                var value = entry(theme); value["status"] = "failed"; value["error"] = error.localizedDescription
                return Shot(entry: value, failed: true)
            }
        }
        var results: [Shot] = []
        for theme in shot.themes {
            var value = entry(theme)
            do {
                apply(theme: theme, store: store)
                try await Task.sleep(for: .milliseconds(700))
                if let message = store.error {
                    // A banner in a help picture would show an error the reader does not have.
                    value["clearedError"] = message
                    store.error = nil
                    try await Task.sleep(for: .milliseconds(200))
                }
                guard target.window.isVisible else { throw MightyError("the \(shot.screen.rawValue) window is not on screen") }
                target.window.displayIfNeeded()
                let url = try store.captureSmokeWindow(target.window, filename: "help-shots/\(language)/\(shot.id)-\(theme).png")
                if let image = NSBitmapImageRep(data: try Data(contentsOf: url)) {
                    value["pixelWidth"] = image.pixelsWide; value["pixelHeight"] = image.pixelsHigh
                }
                if target.window === main, let content = main.contentView?.bounds.size { value["windowContent"] = [content.width, content.height] }
                // A trial, not a help picture: the same view drawn into a bitmap twice its size.
                if [.overview, .agentBasic, .terminalPane].contains(shot.screen), (target.window.screen?.backingScaleFactor ?? 1) < 2 {
                    value["doubleScaleTrial"] = (try? drawDoubled(target.window, to: url.deletingLastPathComponent().appendingPathComponent("trials/\(shot.id)-\(theme)@2x.png")))?.lastPathComponent ?? "failed"
                }
                value["status"] = "ok"
                results.append(Shot(entry: value, failed: false))
            } catch {
                value["status"] = "failed"; value["error"] = error.localizedDescription
                results.append(Shot(entry: value, failed: true))
            }
        }
        await target.cleanup()
        await reset(demo: demo, store: store, main: main)
        return results
    }

    /// The main window at the shot's content size, its top at the top of the screen. A CI
    /// runner's screen is shorter than a help picture, so the capture lets this window
    /// reach below the screen's bottom edge (`allowTallWindows`); the picture is drawn
    /// from the views, not read off the screen, so the part below the edge is there too.
    private static func resize(_ main: NSWindow, to size: HelpShotSize) {
        allowTallWindows(main)
        var frame = main.frameRect(forContentRect: NSRect(x: 0, y: 0, width: size.width, height: size.height))
        let screen = (main.screen ?? NSScreen.main)?.visibleFrame ?? NSRect(x: 0, y: 0, width: size.width, height: size.height)
        frame.origin = NSPoint(x: screen.minX, y: screen.maxY - frame.height)
        main.setFrame(frame, display: true)
    }

    private static var tallWindowsAllowed = false

    /// AppKit keeps a titled window inside its screen (`constrainFrameRect(_:to:)`).
    /// For this capture-only process the main window's class returns the frame it is
    /// given instead; nothing else in the app changes.
    private static func allowTallWindows(_ main: NSWindow) {
        guard !tallWindowsAllowed else { return }
        tallWindowsAllowed = true
        let selector = #selector(NSWindow.constrainFrameRect(_:to:))
        guard let method = class_getInstanceMethod(NSWindow.self, selector) else { return }
        let keep: @convention(block) (NSWindow, NSRect, NSScreen?) -> NSRect = { _, frame, _ in frame }
        class_replaceMethod(type(of: main), selector, imp_implementationWithBlock(keep), method_getTypeEncoding(method))
    }

    /// The window's views drawn into a bitmap of twice their point size.
    private static func drawDoubled(_ window: NSWindow, to url: URL) throws -> URL {
        guard let view = window.contentView?.superview ?? window.contentView else { throw MightyError("no content view") }
        view.layoutSubtreeIfNeeded()
        let bounds = view.bounds
        guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(bounds.width * 2), pixelsHigh: Int(bounds.height * 2), bitsPerSample: 8,
                                            samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { throw MightyError("no bitmap") }
        bitmap.size = bounds.size
        view.cacheDisplay(in: bounds, to: bitmap)
        guard let png = bitmap.representation(using: .png, properties: [:]) else { throw MightyError("no PNG") }
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try png.write(to: url, options: .atomic)
        return url
    }

    /// The screen the capture ran on, for reading the pictures' sizes.
    static func screenInfo(_ main: NSWindow?) -> [String: Any] {
        guard let screen = main?.screen ?? NSScreen.main else { return [:] }
        return ["frame": NSStringFromRect(screen.frame), "visibleFrame": NSStringFromRect(screen.visibleFrame), "backingScale": screen.backingScaleFactor]
    }

    private static func apply(theme: String, store: AppStore) {
        if store.snapshot.theme != theme { store.snapshot.theme = theme }
        NSApp.appearance = NSAppearance(named: theme == "light" ? .aqua : .darkAqua)
    }

    /// Back to the overview: no sheet, popover or focus mode left from the last shot.
    private static func reset(demo: Demo?, store: AppStore, main: NSWindow) async {
        store.showSettings = false; store.resumePicker = nil; store.planDocument = nil
        store.settingsSession = nil; store.sessionInfoSessionID = nil
        store.showsDashboard = false; store.setSidebarCollapsed(false)
        try? await wait("the sheet to close", store: store, timeout: 5) { main.attachedSheet == nil }
        if let demo { try? await select(demo.profile.activeWorkspace, demo: demo, store: store) }
    }

    private static func open(_ screen: HelpScreen, demo: Demo?, store: AppStore, main: NSWindow) async throws -> Target {
        if screen == .welcome {
            guard store.snapshot.workspaces.isEmpty, demo == nil else { throw MightyError("the welcome screen needs the empty profile") }
            try await Task.sleep(for: .milliseconds(400))
            return Target(window: main)
        }
        guard let demo else { throw MightyError("the demo profile is not seeded") }
        let workspace = demo.profile.activeWorkspace
        switch screen {
        case .welcome:
            return Target(window: main)
        case .overview:
            try await select(workspace, demo: demo, store: store)
            return Target(window: main)
        case .sidebarCollapsed:
            store.setSidebarCollapsed(true)
            try await Task.sleep(for: .milliseconds(500))
            return Target(window: main) { store.setSidebarCollapsed(false) }
        case .dashboard:
            store.showsDashboard = true
            try await Task.sleep(for: .milliseconds(500))
            return Target(window: main) { store.showsDashboard = false }
        case .resumeChoice, .resumeList:
            guard let space = store.snapshot.workspaces.first(where: { $0.id == workspace }) else { throw MightyError("the demo workspace is missing") }
            if screen == .resumeList {
                let expected = demo.profile.resumeSessions.filter { $0.provider == "claude" && $0.workspace == workspace }.count
                let found = await store.resumableSessions(for: space, provider: "claude").items.count
                guard found == expected else { throw MightyError("the session list reads \(found) of \(expected) demo records") }
            }
            store.resumePicker = ResumePickerRequest(workspace: space, provider: "claude", stage: screen == .resumeChoice ? .choice : .list)
            let sheet = try await attachedSheet(main, store: store)
            if screen == .resumeList {
                let first = demo.profile.resumeSessions.first?.sessionId ?? ""
                try await wait("the session list", store: store, timeout: 10) { node(sheet, identifier: "resume-row-\(first)") != nil }
            }
            return Target(window: sheet) { store.resumePicker = nil }
        case .agentBasic, .agentMighty, .agentTimeline, .composer, .backgroundWork, .planCard, .questionCard, .permissionCard, .terminalPane, .resultCard:
            let role = screen.paneRole!
            let id = try demo.pane(role)
            try check(screen, id: id, demo: demo, store: store)
            if screen == .agentTimeline { store.updateSession(id) { $0.graphViewMode = .timeline } }
            if screen == .backgroundWork { store.setAgentViewMode(id, mode: "default") }
            store.setPaneFocus(true, sessionId: id)
            try await Task.sleep(for: .milliseconds(600))
            if screen == .agentMighty || screen == .agentTimeline {
                try await wait("the Mighty view", store: store, timeout: 6) { node(main, identifier: "mighty-view-\(screen == .agentMighty ? "diagram" : "timeline")-\(id)") != nil }
            }
            if screen == .resultCard {
                try await wait("the result card's file list", store: store, timeout: 8) { node(main, prefix: "mighty-result-file-") != nil }
            }
            return Target(window: main) {
                if screen == .agentTimeline { store.updateSession(id) { $0.graphViewMode = nil } }
                if screen == .backgroundWork { store.setAgentViewMode(id, mode: "mighty") }
            }
        case .agentMightyOverview:
            let id = try demo.pane("mighty")
            store.setPaneFocus(true, sessionId: id)
            try await wait("the diagram zoom buttons", store: store, timeout: 6) { node(main, identifier: "mighty-zoom-out-\(id)") != nil }
            for _ in 0..<4 {
                guard let zoomOut = node(main, identifier: "mighty-zoom-out-\(id)") else { throw MightyError("the diagram has no zoom-out button") }
                press(zoomOut)
                try await Task.sleep(for: .milliseconds(150))
            }
            try await Task.sleep(for: .milliseconds(400))
            return Target(window: main) { if let reset = node(main, identifier: "mighty-zoom-reset-\(id)") { press(reset) } }
        case .composerSettings:
            let id = try demo.pane("mighty")
            store.setPaneFocus(true, sessionId: id)
            try await Task.sleep(for: .milliseconds(400))
            store.settingsSession = store.snapshot.sessions.first { $0.id == id }
            try await wait("the run settings popover", store: store, timeout: 5) { popover(excluding: main) != nil }
            try await Task.sleep(for: .milliseconds(300))
            guard let window = popover(excluding: main) else { throw MightyError("the run settings popover closed") }
            return Target(window: window) { store.settingsSession = nil }
        case .sessionInfo:
            let id = try demo.pane("mighty")
            store.setPaneFocus(true, sessionId: id)
            try await Task.sleep(for: .milliseconds(400))
            // The context ring opens it, as a click would; the state alone is the fallback.
            if let ring = node(main, identifier: "context-\(id)") { press(ring) } else { store.sessionInfoSessionID = id }
            func details() -> NSWindow? {
                NSApp.windows.first { $0 !== main && $0.isVisible && node($0, identifier: "session-info-\(id)") != nil } ?? popover(excluding: main)
            }
            try await wait("the session details", store: store, timeout: 6) { store.sessionInfoSessionID == id && details() != nil }
            try await Task.sleep(for: .milliseconds(500))
            guard let window = details() else { throw MightyError("the session details did not open") }
            return Target(window: window) { store.sessionInfoSessionID = nil }
        case .planDocument:
            let id = try demo.pane("plan")
            guard let plan = store.toolPermissions[id]?.first(where: { $0.canAnswerPlan })?.plan else { throw MightyError("the plan pane has no plan waiting") }
            store.setPaneFocus(true, sessionId: id)
            try await Task.sleep(for: .milliseconds(300))
            store.planDocument = PlanDocument(id: "help-plan", title: L("plan.card.title"), subtitle: "", plan: plan)
            let sheet = try await attachedSheet(main, store: store)
            return Target(window: sheet) { store.planDocument = nil }
        case .filesPane:
            try await select(workspace, demo: demo, store: store)
            store.openFilePane(workspaceId: workspace)
            let paneId = FilePaneKind.paneId(workspaceId: workspace)
            guard store.snapshot.sessions.contains(where: { $0.id == paneId }), let model = store.filePaneModel(for: workspace) else { throw MightyError("the files pane did not open") }
            model.start()
            try await wait("the file tree", store: store, timeout: 10) { model.children[""] != nil }
            model.toggle(WorkspaceFileEntry(name: "src", relativePath: "src", isDirectory: true))
            try await wait("the src folder", store: store, timeout: 10) { model.children["src"] != nil }
            model.select(WorkspaceFileEntry(name: "README.md", relativePath: "README.md", isDirectory: false))
            try await wait("the README preview", store: store, timeout: 10) { if case .markdown = model.preview { return true }; return false }
            store.setPaneFocus(true, sessionId: paneId)
            try await Task.sleep(for: .milliseconds(600))
            return Target(window: main) {
                store.closeSession(paneId)
                try? await wait("the files pane to close", store: store, timeout: 5) { !store.snapshot.sessions.contains { $0.id == paneId } }
                // The files pane went in beside a pane; the workspace gets its own layout back.
                if let layout = demo.profile.layouts.first(where: { $0.workspace == workspace }) {
                    store.selectWorkspace(workspace)
                    store.setPaneLayoutPreset(layout.preset)
                    demo.modes[workspace] = store.paneLayoutMode(workspace)
                }
            }
        case .companionPet:
            // The pet's own window, as the app shows it; a panel of its own here
            // because a smoke-style run does not open the floating one.
            let id = try demo.pane("mighty")
            store.companion.focus(id)
            let panel = NSPanel(contentRect: NSRect(x: 160, y: 160, width: 282, height: CompanionPanel.tallHeight), styleMask: [.borderless], backing: .buffered, defer: false)
            panel.isReleasedWhenClosed = false
            panel.backgroundColor = .clear; panel.isOpaque = false; panel.hasShadow = false
            panel.contentView = NSHostingView(rootView: CompanionOverlayView(companion: store.companion))
            panel.orderFront(nil)
            try await Task.sleep(for: .milliseconds(700))
            return Target(window: panel) { panel.orderOut(nil); panel.close() }
        case .settingsGeneral, .settingsModels, .settingsStyles, .settingsTools, .settingsCLI, .settingsMobile, .settingsCompanion, .settingsAbout:
            UserDefaults.standard.set(screen.settingsPane!, forKey: "settingsPane")
            store.showSettings = true
            let sheet = try await attachedSheet(main, store: store)
            try await Task.sleep(for: .milliseconds(900))
            return Target(window: sheet) { store.showSettings = false }
        }
    }

    /// Checks that the seeded pane is in the state the screen is meant to show.
    private static func check(_ screen: HelpScreen, id: String, demo: Demo, store: AppStore) throws {
        guard let session = store.snapshot.sessions.first(where: { $0.id == id }) else { throw MightyError("the pane is missing") }
        let waiting = store.toolPermissions[id]?.first
        switch screen {
        case .agentMighty, .agentTimeline, .resultCard:
            guard session.agentViewMode == "mighty", session.graphRuns?.contains(where: { $0.status == "completed" && !$0.resultEntries.isEmpty }) == true else { throw MightyError("the Mighty pane has no finished request") }
        case .backgroundWork:
            guard session.backgroundWork?.waitingOnBackground == true, session.todoProgress != nil else { throw MightyError("the pane has no background work or checklist") }
        case .planCard:
            guard waiting?.canAnswerPlan == true else { throw MightyError("the plan pane has no plan waiting") }
        case .questionCard:
            guard waiting?.canAnswerQuestions == true else { throw MightyError("the question pane has no question waiting") }
        case .permissionCard:
            guard waiting?.canAllow == true else { throw MightyError("the permission pane has no approval waiting") }
        case .terminalPane:
            if let problem = demo.terminalProblem { throw MightyError("the terminal is not ready: \(problem)") }
        default: break
        }
    }

    /// `waitForSmoke` with a message that says what never came.
    private static func wait(_ what: String, store: AppStore, timeout: TimeInterval, _ predicate: () -> Bool) async throws {
        do { try await store.waitForSmoke(timeout: timeout, predicate: predicate) }
        catch { throw MightyError("timed out waiting for \(what)") }
    }

    private static func attachedSheet(_ main: NSWindow, store: AppStore) async throws -> NSWindow {
        try await wait("the sheet", store: store, timeout: 5) { main.attachedSheet?.isVisible == true }
        try await Task.sleep(for: .milliseconds(400))
        guard let sheet = main.attachedSheet else { throw MightyError("the sheet did not open") }
        return sheet
    }

    private static func popover(excluding main: NSWindow) -> NSWindow? {
        NSApp.windows.first { $0 !== main && $0.isVisible && NSStringFromClass(type(of: $0)).contains("Popover") }
    }

    // MARK: Accessibility

    /// The first element with the identifier (or prefix), searched like the GUI smoke's
    /// `smokeAccessibilityElement`: AppKit can leave SwiftUI hosting wrappers out of the
    /// accessibility tree, so the native subviews (and a window's content view) are
    /// walked too. Hidden views are skipped.
    private static func node(_ element: Any, identifier: String? = nil, prefix: String? = nil) -> NSObject? {
        var visited = Set<ObjectIdentifier>()
        func find(_ element: Any, depth: Int) -> NSObject? {
            if let view = element as? NSView, view.isHiddenOrHasHiddenAncestor { return nil }
            guard depth < 128, visited.count < 20_000, let object = element as? NSObject,
                  visited.insert(ObjectIdentifier(object)).inserted else { return nil }
            func value(_ key: String) -> Any? { object.responds(to: NSSelectorFromString(key)) ? object.value(forKey: key) : nil }
            if let id = value("accessibilityIdentifier") as? String, (identifier.map { id == $0 } ?? false) || (prefix.map { id.hasPrefix($0) } ?? false) {
                return object
            }
            var children = value("accessibilityChildren") as? [Any] ?? []
            if let view = element as? NSView { children.append(contentsOf: view.subviews) }
            if let window = element as? NSWindow, let content = window.contentView { children.append(content) }
            for child in children {
                if let found = find(child, depth: depth + 1) { return found }
            }
            return nil
        }
        return find(element, depth: 0)
    }

    private static func press(_ object: NSObject) {
        let selector = NSSelectorFromString("accessibilityPerformPress")
        guard object.responds(to: selector), let implementation = object.method(for: selector) else { return }
        typealias Press = @convention(c) (AnyObject, Selector) -> Bool
        _ = unsafeBitCast(implementation, to: Press.self)(object, selector)
    }
}
