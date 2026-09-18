import AppKit
import Darwin
import MightyCore
import SwiftUI

extension AppStore {
    func runAgentSmokeTest() async {
        let args = ProcessInfo.processInfo.arguments
        guard args.contains("--profile") else { error = "에이전트 검증은 임시 --profile이 필요합니다."; return }
        var result: [String: Any] = ["passed": false, "aiRequestSent": false, "systemNotificationSent": false]
        do {
            await refreshRuntime()
            let directory = dataDirectory.appendingPathComponent("Agent Studio")
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let workspace = try await repository.approveWorkspace(Workspace(name: "Agent Studio", path: directory.path))
            addWorkspace(workspace)
            guard let claude = snapshot.activeSessionId else { throw MightyError("Claude fixture missing") }
            addSession(kind: "claude", provider: "codex")
            guard let codex = snapshot.activeSessionId else { throw MightyError("Codex fixture missing") }
            setPaneLayoutPreset("columns")
            let checks = AgentMarkdownDiagnostics.checks()
            result["markdown"] = checks
            guard checks.values.allSatisfy({ $0 }) else { throw MightyError("Markdown fixture failed") }
            let composerPresentation = await ComposerPresentationDiagnostics.run(store: self)
            result["composerPresentation"] = composerPresentation
            guard composerPresentation["passed"] as? Bool == true else { throw MightyError("Composer presentation failed") }
            let sessionInfo = await SessionInfoDiagnostics.run(store: self)
            result["sessionInfo"] = sessionInfo
            guard sessionInfo["passed"] as? Bool == true else { throw MightyError("Session info presentation failed") }
            let petMotion = await CompanionPetDiagnostics.run(store: self)
            result["petMotion"] = petMotion
            guard petMotion["passed"] as? Bool == true else { throw MightyError("Pet motion presentation failed") }
            let mightyGraph = await MightyGraphDiagnostics.run(store: self)
            result["mightyGraph"] = mightyGraph
            guard mightyGraph["passed"] as? Bool == true else { throw MightyError("Mighty graph presentation failed") }
            let plugins = await ClaudePluginDiagnostics.run(store: self)
            result["plugins"] = plugins
            guard plugins["passed"] as? Bool == true else { throw MightyError("Claude plugin browser failed") }
            let transcript = await AgentTranscriptDiagnostics.run(screenshotDirectory: dataDirectory)
            result["transcript"] = transcript
            guard transcript["passed"] as? Bool == true else { throw MightyError("Native transcript selection failed") }
            apply(RunEvent(sessionId: claude, type: "log", entry: LogEntry(kind: "assistant", text: AgentMarkdownDiagnostics.fixture)))
            apply(RunEvent(sessionId: codex, type: "status", status: "running"))
            apply(RunEvent(sessionId: codex, type: "log", entry: LogEntry(kind: "user", text: "웹에서 Claude Mods를 조사하고\n구현과 검증 결과를 정리해 줘.")))
            let tool = AgentActivity(id: "test-tool", provider: "codex", kind: "command", state: "running", toolName: "Bash", summary: "swift test --filter AgentActivityTests")
            apply(RunEvent(sessionId: codex, type: "activity", activity: tool))
            apply(RunEvent(sessionId: codex, type: "log", entry: LogEntry(id: "test-tool-log", kind: "system", text: tool.summary, activity: tool)))
            let initialCompletions = companion.completionCount
            var done = tool; done.state = "completed"; done.output = "12 tests passed"
            apply(RunEvent(sessionId: codex, type: "activity", activity: done))
            guard companion.current?.input == "웹에서 Claude Mods를 조사하고 구현과 검증 결과를 정리해 줘.", companion.current?.summary == done.summary else { throw MightyError("Pet input was replaced by tool output") }
            guard companion.agents.first(where: { $0.id == claude })?.input == nil else { throw MightyError("Pet input leaked into another agent") }
            result["petInputAndActivitySeparated"] = true
            let restoredCompanion = AgentCompanion()
            restoredCompanion.refresh(snapshot)
            guard restoredCompanion.agents.first(where: { $0.id == codex })?.input == companion.current?.input else { throw MightyError("Pet request did not restore from the conversation") }
            result["petInputRestores"] = true
            let timerProbe = AgentCompanion()
            let clockStart = Date(timeIntervalSince1970: 1_000)
            var timerSnapshot = snapshot
            guard let timerIndex = timerSnapshot.sessions.firstIndex(where: { $0.id == codex }) else { throw MightyError("Timer fixture missing") }
            func startTimer(at date: Date) {
                timerSnapshot.sessions[timerIndex].beginRunTiming(at: date)
                timerSnapshot.sessions[timerIndex].status = "running"
                timerProbe.beginRun(sessionID: codex, at: date)
                timerProbe.refresh(timerSnapshot)
            }
            func timerEvent(_ event: RunEvent, at date: Date) {
                timerSnapshot.sessions[timerIndex].recordRunTiming(event, at: date)
                if let status = event.status, event.type == "status" { timerSnapshot.sessions[timerIndex].status = status }
                timerProbe.receive(event, snapshot: timerSnapshot, at: date)
            }
            startTimer(at: clockStart)
            timerEvent(RunEvent(sessionId: codex, type: "status", status: "running"), at: clockStart.addingTimeInterval(3))
            timerEvent(RunEvent(sessionId: codex, type: "activity", activity: done), at: clockStart.addingTimeInterval(8))
            guard timerProbe.agents.first(where: { $0.id == codex })?.timing?.elapsed(at: clockStart.addingTimeInterval(10)) == 10 else { throw MightyError("Run clock reset during startup or tool completion") }
            timerEvent(RunEvent(sessionId: codex, type: "status", status: "completed"), at: clockStart.addingTimeInterval(12))
            timerEvent(RunEvent(sessionId: codex, type: "status", status: "completed"), at: clockStart.addingTimeInterval(20))
            guard timerProbe.agents.first(where: { $0.id == codex })?.timing?.elapsed(at: clockStart.addingTimeInterval(100)) == 12 else { throw MightyError("Completed run clock kept ticking") }
            startTimer(at: clockStart.addingTimeInterval(100))
            guard timerProbe.agents.first(where: { $0.id == codex })?.timing?.elapsed(at: clockStart.addingTimeInterval(102)) == 2 else { throw MightyError("New request retained the previous clock") }
            var timerWait = tool; timerWait.state = "waiting"
            timerEvent(RunEvent(sessionId: codex, type: "activity", activity: timerWait), at: clockStart.addingTimeInterval(104))
            guard timerProbe.agents.first(where: { $0.id == codex })?.timing?.elapsed(at: clockStart.addingTimeInterval(109)) == 9 else { throw MightyError("Permission wait paused the run clock") }
            timerEvent(RunEvent(sessionId: codex, type: "status", status: "stopped"), at: clockStart.addingTimeInterval(110))
            guard timerProbe.agents.first(where: { $0.id == codex })?.timing?.elapsed(at: clockStart.addingTimeInterval(200)) == 10 else { throw MightyError("Stopped run clock kept ticking") }
            result["runTimingLifecycle"] = true
            guard var bubbleAgent = companion.agents.first(where: { $0.id == codex }) else { throw MightyError("Bubble fixture missing") }
            let bubbleProbe = CompanionBubbleController(delay: .milliseconds(60))
            bubbleProbe.synchronize(CompanionBubbleIdentity(bubbleAgent))
            guard bubbleProbe.isVisible else { throw MightyError("New task did not show its bubble") }
            bubbleProbe.toggle()
            bubbleAgent.summary = "다음 도구 실행"
            bubbleProbe.synchronize(CompanionBubbleIdentity(bubbleAgent))
            guard !bubbleProbe.isVisible else { throw MightyError("Tool output overrode a hidden bubble") }
            bubbleAgent.status = "completed"
            bubbleProbe.synchronize(CompanionBubbleIdentity(bubbleAgent))
            guard bubbleProbe.isVisible else { throw MightyError("Completion did not show its result") }
            try await waitForSmoke(timeout: 2) { !bubbleProbe.isVisible }
            guard !bubbleProbe.isVisible else { throw MightyError("Completed bubble did not auto-hide") }
            bubbleProbe.toggle()
            try await Task.sleep(for: .milliseconds(100))
            guard bubbleProbe.isVisible else { throw MightyError("Manually reopened bubble was hidden") }
            bubbleProbe.toggle()
            bubbleAgent.status = "running"
            bubbleAgent.timing = AgentRunTiming()
            bubbleProbe.synchronize(CompanionBubbleIdentity(bubbleAgent))
            guard bubbleProbe.isVisible else { throw MightyError("Next request did not reopen the bubble") }
            bubbleAgent.status = "completed"
            bubbleProbe.synchronize(CompanionBubbleIdentity(bubbleAgent))
            bubbleProbe.show()
            try await Task.sleep(for: .milliseconds(100))
            guard bubbleProbe.isVisible else { throw MightyError("Explicitly enabling a bubble kept its auto-hide deadline") }
            result["petBubbleAutoHideAndToggle"] = true
            guard companion.completionCount == initialCompletions, companion.runningCount == 1 else { throw MightyError("Tool completion ended entire run") }
            result["toolCompletionDoesNotNotify"] = true
            let waiting = AgentActivity(id: "waiting-tool", provider: "codex", kind: "tool", state: "waiting", toolName: "Fixture", summary: "권한 판단 대기")
            apply(RunEvent(sessionId: codex, type: "activity", activity: waiting))
            apply(RunEvent(sessionId: codex, type: "activity", activity: done))
            guard companion.current?.status == "waiting", companion.current?.activity?.id == waiting.id else { throw MightyError("Parallel tool result hid a waiting tool") }
            var settled = waiting; settled.state = "completed"
            apply(RunEvent(sessionId: codex, type: "activity", activity: settled))
            guard companion.current?.status == "running" else { throw MightyError("Settled tool remained waiting") }
            result["parallelWaitingPreserved"] = true
            apply(RunEvent(sessionId: codex, type: "status", status: "completed"))
            apply(RunEvent(sessionId: codex, type: "status", status: "completed"))
            guard companion.completionCount == initialCompletions + 1 else { throw MightyError("Completion not deduplicated") }
            apply(RunEvent(sessionId: codex, type: "status", status: "running"))
            apply(RunEvent(sessionId: codex, type: "status", status: "error"))
            guard companion.completionCount == initialCompletions + 1 else { throw MightyError("Error produced success alert") }
            result["completionOncePerRun"] = true
            apply(RunEvent(sessionId: codex, type: "status", status: "running"))
            apply(RunEvent(sessionId: codex, type: "activity", activity: tool))
            apply(RunEvent(sessionId: codex, type: "log", entry: LogEntry(kind: "assistant", text: "## 테스트 실행 중\n\n변경된 파일의 **검증 결과**를 확인하고 있어요.\n\n- 컴파일 확인\n- 회귀 테스트\n- 결과 정리")))
            selectSession(claude)
            companion.focus(codex)
            guard snapshot.activeSessionId == codex, snapshot.activeWorkspaceId == workspace.id else { throw MightyError("Pet focus failed") }
            result["petFocus"] = true
            guard let pet = companion.pets.first(where: { $0.id == "mighty-raccoon" }) else { throw MightyError("Bundled superhero pet missing") }
            guard pet.frames.map(\.count) == CompanionPet.frameCounts else { throw MightyError("Pet frame counts invalid") }
            result["petFrames"] = pet.frames.map(\.count)
            let installed = try CompanionPet.install(from: pet.source.deletingLastPathComponent(), into: dataDirectory.appendingPathComponent("pets"))
            guard installed.pixelWidth == 1536, installed.pixelHeight == 1872 else { throw MightyError("Pet import failed") }
            result["petImport"] = true
            let malicious = dataDirectory.appendingPathComponent("invalid-pet.json")
            try Data("{\"spritesheetPath\":\"../outside.webp\"}".utf8).write(to: malicious)
            do { _ = try CompanionPet.load(from: malicious, id: "invalid"); throw MightyError("Unsafe path accepted") }
            catch { guard error.localizedDescription.contains("폴더 안") else { throw error } }
            result["petPathTraversalRejected"] = true
            let previous = companion.preferences
            companion.preferences.showsTask = false
            let saved = try JSONDecoder().decode(CompanionPreferences.self, from: Data(contentsOf: dataDirectory.appendingPathComponent("companion-settings.json")))
            guard !saved.showsTask else { throw MightyError("Companion settings not persisted") }
            companion.preferences = previous
            result["preferencesPersist"] = true
            try await Task.sleep(for: .milliseconds(700))
            guard let window = NSApp.windows.first(where: { !($0 is NSPanel) && $0.isVisible }) else { throw MightyError("App window missing") }
            result["screenshot"] = try captureSmokeWindow(window, filename: "agent-markdown.png").path
            apply(RunEvent(sessionId: claude, type: "status", status: "running"))
            let permission = ToolPermissionRequest(id: "permission-web", runId: "fixture-run", toolUseId: "tool-web", toolName: "WebSearch", inputJSON: "{\"query\":\"Claude Mods 공식 문서\"}", summary: "Claude Mods 공식 문서 검색")
            apply(RunEvent(sessionId: claude, type: "permission", permission: permission))
            apply(RunEvent(sessionId: claude, type: "permission", permission: permission))
            guard toolPermissions[claude]?.count == 1 else { throw MightyError("Permission prompt duplicated") }
            var stale = permission; stale.runId = "previous-run"; stale.state = "cancelled"
            apply(RunEvent(sessionId: claude, type: "permission", permission: stale))
            guard toolPermissions[claude]?.count == 1 else { throw MightyError("A stale resolution removed a new prompt") }
            apply(RunEvent(sessionId: "unknown-pane", type: "permission", permission: permission))
            guard toolPermissions["unknown-pane"] == nil else { throw MightyError("Permission accepted for an unknown pane") }
            selectSession(claude)
            try await Task.sleep(for: .milliseconds(250))
            result["permissionScreenshot"] = try captureSmokeWindow(window, filename: "agent-permission.png").path
            var resolved = permission; resolved.state = "denied"
            apply(RunEvent(sessionId: claude, type: "permission", permission: resolved))
            guard toolPermissions[claude]?.isEmpty == true else { throw MightyError("Resolved permission remained visible") }
            apply(RunEvent(sessionId: claude, type: "permission", permission: permission))
            apply(RunEvent(sessionId: claude, type: "status", status: "stopped"))
            guard toolPermissions[claude] == nil else { throw MightyError("Stopped session retained an actionable prompt") }
            result["permissionPromptLifecycle"] = true
            selectSession(codex)
            let panel = NSPanel(contentRect: NSRect(x: 150, y: 150, width: 282, height: CompanionPanel.baseHeight), styleMask: [.borderless], backing: .buffered, defer: false)
            panel.backgroundColor = .clear; panel.isOpaque = false
            panel.contentView = NSHostingView(rootView: CompanionOverlayView(companion: companion))
            panel.orderFront(nil)
            try await Task.sleep(for: .milliseconds(250))
            result["petScreenshot"] = try captureSmokeWindow(panel, filename: "agent-pet.png").path
            panel.orderOut(nil)
            companion.showsStatus = true
            try await Task.sleep(for: .milliseconds(250))
            guard let statusWindow = NSApp.windows.first(where: { $0.isVisible && NSStringFromClass(type(of: $0)).contains("Popover") }) else { throw MightyError("Status popover did not open") }
            result["statusScreenshot"] = try captureSmokeWindow(statusWindow, filename: "agent-status.png").path
            result["statusPopoverVisible"] = true
            companion.focus(claude)
            try await Task.sleep(for: .milliseconds(200))
            guard window.isKeyWindow, snapshot.activeSessionId == claude else { throw MightyError("Status click did not focus the main agent window") }
            result["statusClickFocusesMainWindow"] = true
            showSettings = true
            try await Task.sleep(for: .milliseconds(350))
            guard let settings = window.attachedSheet else { throw MightyError("Companion settings did not open") }
            result["settingsScreenshot"] = try captureSmokeWindow(settings, filename: "agent-settings.png").path
            showSettings = false
            apply(RunEvent(sessionId: codex, type: "status", status: "stopped"))
            try await flush()
            let restoredTiming = try await StateRepository(directory: dataDirectory, legacyStateURL: nil).load()
            snapshot = restoredTiming
            let freshCompanion = AgentCompanion()
            freshCompanion.refresh(restoredTiming)
            freshCompanion.reloadPets()
            guard let savedClock = restoredTiming.sessions.first(where: { $0.id == codex })?.runTiming,
                  savedClock.finishedAt != nil,
                  freshCompanion.agents.first(where: { $0.id == codex })?.timing == savedClock,
                  companion.agents.first(where: { $0.id == codex })?.timing == savedClock else { throw MightyError("Saved clock did not restore into pane and pet state") }
            selectSession(codex)
            try await waitForSmoke(timeout: 2) { self.timingViewExists(window, identifier: "agent-elapsed-\(codex)") }
            result["restoredPaneTimerVisible"] = true
            panel.contentView = NSHostingView(rootView: CompanionOverlayView(companion: freshCompanion))
            panel.orderFront(nil)
            try await waitForSmoke(timeout: 2) { self.timingViewExists(panel, identifier: "pet-task-bubble") }
            result["restoredTimingScreenshot"] = try captureSmokeWindow(panel, filename: "agent-restored-time.png").path
            guard let petClock = freshCompanion.current?.timing, petClock.finishedAt != nil else { throw MightyError("Restored pet clock missing") }
            let expectedClock = "실행 시간 " + petClock.label()
            result["restoredPetExpectedClock"] = expectedClock
            try await waitForSmoke(timeout: 2) { self.timingViewExists(panel, identifier: "pet-task-bubble", containing: expectedClock) }
            result["restoredPetTimerVisible"] = true
            panel.orderOut(nil)
            result["runTimingPersists"] = true
            result["rename"] = try await verifyRenameSmoke(window: window)
            result["headerSettings"] = try await verifyHeaderSettingsSmoke(window: window)
            result["passed"] = true
        } catch { result["error"] = error.localizedDescription }
        do { try JSONSerialization.data(withJSONObject: result, options: [.prettyPrinted, .sortedKeys]).write(to: dataDirectory.appendingPathComponent("agent-smoke-result.json")) }
        catch { NSLog("Agent smoke report: %@", error.localizedDescription) }
        if args.contains("--smoke-exit") { await shutdown(); Darwin.exit(result["passed"] as? Bool == true ? 0 : 1) }
    }

    private func timingViewExists(_ element: Any, identifier: String, containing text: String? = nil, depth: Int = 0) -> Bool {
        guard depth < 40 else { return false }
        let children: [Any]
        if let node = element as? any NSAccessibilityProtocol {
            if node.accessibilityIdentifier() == identifier, node.accessibilityFrame().height > 0 {
                return text.map { node.accessibilityLabel()?.contains($0) == true } ?? true
            }
            children = node.accessibilityChildren() ?? []
        } else if let node = element as? NSObject {
            if node.responds(to: NSSelectorFromString("accessibilityIdentifier")), node.value(forKey: "accessibilityIdentifier") as? String == identifier,
               node.responds(to: NSSelectorFromString("accessibilityFrame")), let frame = (node.value(forKey: "accessibilityFrame") as? NSValue)?.rectValue, frame.height > 0 {
                guard let text else { return true }
                return node.responds(to: NSSelectorFromString("accessibilityLabel")) && (node.value(forKey: "accessibilityLabel") as? String)?.contains(text) == true
            }
            children = node.responds(to: NSSelectorFromString("accessibilityChildren")) ? node.value(forKey: "accessibilityChildren") as? [Any] ?? [] : []
        } else { return false }
        return children.contains { timingViewExists($0, identifier: identifier, containing: text, depth: depth + 1) }
    }
}
