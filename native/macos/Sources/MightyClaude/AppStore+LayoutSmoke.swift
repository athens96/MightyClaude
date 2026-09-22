import AppKit
import Darwin
import Foundation
import GhosttyTerminal
import MightyCore

extension AppStore {
    /// Isolated integration check: the same store operations used by docking,
    /// real native views, and a real PTY. Never sends a model request.
    func runLayoutSmokeTest() async {
        let arguments = ProcessInfo.processInfo.arguments
        guard arguments.contains("--profile") else {
            error = "레이아웃 검증에는 임시 --profile 폴더가 필요합니다."
            if arguments.contains("--smoke-exit") { Darwin.exit(1) }
            return
        }
        var result: [String: Any] = ["passed": false, "aiRequestSent": false]
        var stage = "prepare"
        var window: NSWindow?
        do {
            let folder = dataDirectory.appendingPathComponent("Layout Workspace", isDirectory: true)
            let otherFolder = dataDirectory.appendingPathComponent("Other Workspace", isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            try FileManager.default.createDirectory(at: otherFolder, withIntermediateDirectories: true)
            let workspace = try await repository.approveWorkspace(Workspace(name: "Layout Studio", path: folder.path))
            addWorkspace(workspace)
            let claude = try layoutSmokeActiveID()
            addSession(kind: "claude", provider: "codex")
            let codex = try layoutSmokeActiveID()
            addSession(kind: "claude", provider: "gemini")
            let gemini = try layoutSmokeActiveID()
            addSession(kind: "shell")
            let shell = try layoutSmokeActiveID()
            drafts[claude] = "레이아웃을 옮겨도\n이 초안은 유지됩니다."
            let attachment = try AttachmentSupport.make(name: "layout-note.txt", data: Data("Layout fixture".utf8))
            attachmentDrafts[claude] = [attachment]
            setPaneLayoutPreset("tabs")
            selectSession(shell)
            await ensureLocalTerminal(shell)
            try await waitForSmoke(timeout: 15) { self.localTerminals[shell]?.ready == true && self.localTerminals[shell]?.view.window != nil }
            guard let terminal = localTerminals[shell], let nativeWindow = terminal.view.window else { throw MightyError("검증 터미널이 표시되지 않았습니다.") }
            window = nativeWindow
            let pidFile = folder.appendingPathComponent("layout-shell-pid.txt")
            let command = "printf '%s\\n' \"$$\" > \(layoutSmokeQuote(pidFile.path)); printf '\\033[2J\\033[H\\033[36mMightyClaude · Ghostty\\033[0m\\nDocking keeps this shell alive.\\n'; PROMPT='layout> '"
            try layoutSmokeCommand(terminal, command)
            try await waitForSmoke(timeout: 5) { self.layoutSmokePID(pidFile) != nil }
            guard let pid = layoutSmokePID(pidFile) else { throw MightyError("셸 PID가 없습니다.") }
            result["shellPID"] = Int(pid)
            let initialRows = terminal.grid?.rows ?? 0
            result["initialTerminalBounds"] = NSStringFromRect(terminal.view.bounds)

            stage = "tabs-and-splits"
            let zoneSize = CGSize(width: 600, height: 400)
            let zoneCases: [(CGPoint, PaneDockZone)] = [
                (CGPoint(x: 300, y: 200), .center), (CGPoint(x: 12, y: 200), .left),
                (CGPoint(x: 588, y: 200), .right), (CGPoint(x: 300, y: 10), .top),
                (CGPoint(x: 300, y: 390), .bottom),
            ]
            guard zoneCases.allSatisfy({ PaneDockZone.resolve(location: $0.0, in: zoneSize) == $0.1 }) else { throw MightyError("드롭 위치 판정이 올바르지 않습니다.") }
            result["dropZones"] = true
            guard let allTabs = layoutForWorkspace(workspace.id), allTabs.kind == "tabs" else { throw MightyError("탭 프리셋이 만들어지지 않았습니다.") }
            try await Task.sleep(for: .milliseconds(200))
            let rightEdge = try layoutSmokeDropPoint(groupId: allTabs.id, window: nativeWindow, zone: .right)
            let preview = try layoutSmokeNativeDrag(sessionId: codex, window: nativeWindow, destination: rightEdge, previewGroupId: allTabs.id, previewFilename: "layout-drag-preview.png")
            try await waitForSmoke(timeout: 5) { self.layoutForWorkspace(workspace.id)?.kind == "split" }
            result["nativeMouseEdgeSplit"] = true
            result["nativeDragPreviewBeforeDrop"] = preview
            guard preview else { throw MightyError("드래그 도중 분할 미리보기가 그려지지 않았습니다.") }
            guard let right = layoutSmokeGroup(workspace.id, containing: codex) else { throw MightyError("오른쪽 분할이 없습니다.") }
            try await Task.sleep(for: .milliseconds(200))
            let center = try layoutSmokeDropPoint(groupId: right.id, window: nativeWindow, zone: .center)
            _ = try layoutSmokeNativeDrag(sessionId: gemini, window: nativeWindow, destination: center, previewGroupId: right.id)
            guard layoutSmokeGroup(workspace.id, containing: codex)?.sessionIds == [codex, gemini] else { throw MightyError("실제 마우스 드래그로 탭을 합치지 못했습니다.") }
            result["nativeMouseTabMerge"] = true
            try await Task.sleep(for: .milliseconds(200))
            guard let firstTab = PaneDockDragCoordinator.shared.tab(sessionId: codex, in: nativeWindow) else { throw MightyError("순서 변경 대상 탭이 없습니다.") }
            let beforeCodex = firstTab.convert(NSPoint(x: 3, y: firstTab.bounds.midY), to: nil)
            _ = try layoutSmokeNativeDrag(sessionId: gemini, window: nativeWindow, destination: beforeCodex)
            guard let reordered = layoutSmokeGroup(workspace.id, containing: codex), reordered.sessionIds == [gemini, codex] else { throw MightyError("탭 묶기 또는 순서 변경에 실패했습니다.") }
            result["nativeMouseTabReorder"] = true
            guard let left = layoutSmokeGroup(workspace.id, containing: shell) else { throw MightyError("왼쪽 분할이 없습니다.") }
            try await Task.sleep(for: .milliseconds(200))
            _ = try layoutSmokeNativeDrag(sessionId: claude, window: nativeWindow, destination: layoutSmokeDropPoint(groupId: left.id, window: nativeWindow, zone: .bottom), previewGroupId: left.id)
            guard let root = layoutForWorkspace(workspace.id), root.kind == "split", layoutSmokeLeaves(root).count == 3 else { throw MightyError("중첩 분할이 만들어지지 않았습니다.") }
            try await Task.sleep(for: .milliseconds(200))
            let beforeCancellation = snapshot.paneLayouts
            _ = try layoutSmokeNativeDrag(sessionId: gemini, window: nativeWindow, destination: layoutSmokeDropPoint(groupId: left.id, window: nativeWindow, zone: .center), cancel: true)
            guard snapshot.paneLayouts == beforeCancellation, draggedPane == nil, PaneDockDragCoordinator.shared.target == nil else { throw MightyError("Escape 취소 후 드래그 상태가 남았습니다.") }
            result["nativeMouseEscapeCancels"] = true
            _ = try layoutSmokeNativeDrag(sessionId: gemini, window: nativeWindow, destination: NSPoint(x: -30, y: -30))
            guard snapshot.paneLayouts == beforeCancellation, draggedPane == nil else { throw MightyError("창 바깥 드롭이 배치를 변경했습니다.") }
            result["nativeMouseOutsideDropCancels"] = true
            result["nativeMouseHitTargetVerified"] = true
            result["nativeMouseDragStarts"] = PaneDockDragCoordinator.shared.nativeDragStarts
            result["nativeMouseDragCompletions"] = PaneDockDragCoordinator.shared.nativeDragCompletions
            resizePaneSplit(root.id, ratio: 0.58)
            guard abs((layoutForWorkspace(workspace.id)?.ratio ?? 0) - 0.58) < 0.001 else { throw MightyError("분할 비율이 반영되지 않았습니다.") }
            selectSession(codex)
            try await Task.sleep(for: .milliseconds(350))
            let resizedFile = folder.appendingPathComponent("split-grid.txt")
            try layoutSmokeCommand(terminal, "/bin/stty size > \(layoutSmokeQuote(resizedFile.path)); printf '\\033[36mTerminal retained after docking\\033[0m\\n'")
            try await waitForSmoke(timeout: 5) { FileManager.default.fileExists(atPath: resizedFile.path) }
            try await Task.sleep(for: .milliseconds(350))
            result["splitTerminalBounds"] = NSStringFromRect(terminal.view.bounds)
            result["splitTerminalHostBounds"] = terminal.view.superview.map { NSStringFromRect($0.bounds) } ?? "detached"
            result["splitTerminalVisibleRect"] = NSStringFromRect(terminal.view.visibleRect)
            result["splitTerminalAttached"] = terminal.view.window === nativeWindow && terminal.view.superview != nil
            result["splitTerminalGrid"] = try String(contentsOf: resizedFile, encoding: .utf8)
            result["terminalRowsReduced"] = terminal.grid.map { $0.rows < initialRows } ?? false
            result["splitScreenshot"] = try captureSmokeWindow(nativeWindow, filename: "layout-splits.png").path
            guard terminal.view.window === nativeWindow, terminal.view.superview != nil,
                  terminal.grid.map({ $0.rows < initialRows }) == true else { throw MightyError("분할된 터미널의 화면 연결 또는 PTY 높이가 갱신되지 않았습니다.") }
            result["tabsReordered"] = true
            result["nestedSplits"] = true
            result["splitRatio"] = 0.58

            stage = "draft-and-terminal-retention"
            guard drafts[claude] == "레이아웃을 옮겨도\n이 초안은 유지됩니다.", attachmentDrafts[claude] == [attachment] else { throw MightyError("이동 중 초안 또는 첨부가 바뀌었습니다.") }
            guard let chatGroup = layoutSmokeGroup(workspace.id, containing: claude) else { throw MightyError("채팅 탭이 없습니다.") }
            movePane(sessionId: shell, targetGroupId: chatGroup.id, placement: "tab", beforeSessionId: nil)
            selectSession(claude)
            try await Task.sleep(for: .milliseconds(250))
            guard localTerminals[shell] === terminal, !terminal.disposed, Darwin.kill(pid, 0) == 0 else { throw MightyError("숨은 터미널이 종료되었습니다.") }
            let other = try await repository.approveWorkspace(Workspace(name: "Other Workspace", path: otherFolder.path))
            addWorkspace(other)
            let otherID = try layoutSmokeActiveID()
            addSession(kind: "claude", provider: "gemini")
            let otherSelectedID = try layoutSmokeActiveID()
            setPaneLayoutPreset("tabs")
            try await Task.sleep(for: .milliseconds(200))
            selectSession(shell)
            try await waitForSmoke(timeout: 5) { terminal.view.window != nil }
            let returnedPID = folder.appendingPathComponent("returned-pid.txt")
            try layoutSmokeCommand(terminal, "printf '%s\\n' \"$$\" > \(layoutSmokeQuote(returnedPID.path))")
            try await waitForSmoke(timeout: 5) { self.layoutSmokePID(returnedPID) == pid }
            guard localTerminals[shell] === terminal, !terminal.disposed else { throw MightyError("터미널 화면이 다시 생성되었습니다.") }
            result["sameTerminalAndPID"] = true
            result["draftAndAttachmentRetained"] = true

            stage = "workspace-tab-group-settings"
            selectSession(gemini)
            guard let destination = layoutSmokeGroup(workspace.id, containing: gemini), let beforeAdding = layoutForWorkspace(workspace.id) else { throw MightyError("활성 탭 그룹이 없습니다.") }
            let groupCount = layoutSmokeLeaves(beforeAdding).count
            addSession(kind: "claude", provider: "claude")
            let addedTab = try layoutSmokeActiveID()
            guard layoutSmokeGroup(workspace.id, containing: addedTab)?.id == destination.id,
                  layoutForWorkspace(workspace.id).map({ layoutSmokeLeaves($0).count }) == groupCount else { throw MightyError("새 실행 창이 활성 그룹의 탭으로 추가되지 않았습니다.") }
            result["newPaneJoinsActiveGroup"] = true
            closeSession(addedTab)
            try await waitForSmoke(timeout: 5) { !self.snapshot.sessions.contains { $0.id == addedTab } }
            addSession(kind: "claude", provider: "codex", targetGroupId: destination.id, placement: "bottom")
            let addedSplit = try layoutSmokeActiveID()
            guard layoutForWorkspace(workspace.id).map({ layoutSmokeLeaves($0).count }) == groupCount + 1,
                  layoutSmokeGroup(workspace.id, containing: gemini)?.sessionIds.contains(codex) == true else { throw MightyError("기존 탭 그룹을 유지하면서 새 분할을 추가하지 못했습니다.") }
            result["newSplitPreservesExistingTabs"] = true
            closeSession(addedSplit)
            try await waitForSmoke(timeout: 5) { !self.snapshot.sessions.contains { $0.id == addedSplit } }
            setPaneFocus(true, sessionId: gemini)
            let retainedTree = layoutForWorkspace(workspace.id)
            selectWorkspace(other.id)
            guard activePaneLayoutMode == "tabs", snapshot.activeSessionId == otherSelectedID else { throw MightyError("다른 워크스페이스의 보기 또는 마지막 선택 탭이 복원되지 않았습니다.") }
            setPaneLayoutPreset("grid")
            guard paneLayoutMode(workspace.id) == "focus", layoutForWorkspace(workspace.id) == retainedTree else { throw MightyError("다른 워크스페이스의 프리셋이 원래 배치를 변경했습니다.") }
            selectWorkspace(workspace.id)
            guard activePaneLayoutMode == "focus", snapshot.activeSessionId == gemini else { throw MightyError("워크스페이스별 집중 보기와 선택 그룹이 유지되지 않았습니다.") }
            togglePaneFocus()
            guard activePaneLayoutMode == "custom", layoutForWorkspace(workspace.id) == retainedTree else { throw MightyError("집중 보기에서 원래 분할로 돌아오지 못했습니다.") }
            guard localTerminals[shell] === terminal, !terminal.disposed, Darwin.kill(pid, 0) == 0 else { throw MightyError("탭 추가 또는 워크스페이스 설정 변경이 PTY를 재시작했습니다.") }
            result["workspaceModesIndependent"] = true
            result["workspaceLastSelectionRestored"] = true

            stage = "workspace-isolation-and-close"
            guard let target = layoutSmokeGroup(workspace.id, containing: shell) else { throw MightyError("이동 대상을 찾지 못했습니다.") }
            let beforeInvalidMove = snapshot.paneLayouts
            let beforeInvalidSelection = snapshot.activeSessionId
            movePane(sessionId: otherID, targetGroupId: target.id, placement: "left", beforeSessionId: nil)
            guard snapshot.paneLayouts == beforeInvalidMove, snapshot.activeSessionId == beforeInvalidSelection else { throw MightyError("다른 워크스페이스의 실행 창이 이동됐습니다.") }
            closeSession(codex)
            try await waitForSmoke(timeout: 5) { !self.snapshot.sessions.contains { $0.id == codex } }
            guard let pruned = layoutForWorkspace(workspace.id), !layoutSmokeLeaves(pruned).flatMap(\.sessionIds).contains(codex) else { throw MightyError("닫힌 탭이 배치에 남았습니다.") }
            result["crossWorkspaceMoveRejected"] = true
            result["closedTabPruned"] = true

            stage = "save-and-restore"
            selectSession(claude)
            try await flush()
            let restored = try await StateRepository(directory: dataDirectory, legacyStateURL: nil).load()
            guard restored.paneLayouts == snapshot.paneLayouts,
                  restored.paneLayoutModes == snapshot.paneLayoutModes,
                  restored.paneLayoutActiveSessionIds == snapshot.paneLayoutActiveSessionIds else { throw MightyError("저장 후 복원한 배치 또는 워크스페이스별 보기 상태가 다릅니다.") }
            result["savedLayoutRestored"] = true
            try await Task.sleep(for: .milliseconds(250))
            result["tabsScreenshot"] = try captureSmokeWindow(nativeWindow, filename: "layout-tabs.png").path
            result["passed"] = true
        } catch {
            result["failedStage"] = stage
            result["error"] = error.localizedDescription
            self.error = "레이아웃 검증 실패: \(error.localizedDescription)"
            if let window, let screenshot = try? captureSmokeWindow(window, filename: "layout-failure.png") { result["failureScreenshot"] = screenshot.path }
        }
        do {
            try FileManager.default.createDirectory(at: dataDirectory, withIntermediateDirectories: true)
            try JSONSerialization.data(withJSONObject: result, options: [.prettyPrinted, .sortedKeys])
                .write(to: dataDirectory.appendingPathComponent("layout-smoke-result.json"), options: .atomic)
        } catch { result["passed"] = false }
        if arguments.contains("--smoke-exit") {
            await shutdown()
            Darwin.exit(result["passed"] as? Bool == true ? 0 : 1)
        }
    }

    private func layoutSmokeActiveID() throws -> String {
        guard let id = snapshot.activeSessionId else { throw MightyError("실행 창이 없습니다.") }
        return id
    }
    private func layoutSmokeLeaves(_ node: PaneLayoutNode) -> [PaneLayoutNode] {
        node.kind == "tabs" ? [node] : node.children.flatMap(layoutSmokeLeaves)
    }
    private func layoutSmokeGroup(_ workspace: String, containing id: String) -> PaneLayoutNode? {
        layoutForWorkspace(workspace).flatMap { layoutSmokeLeaves($0).first { $0.sessionIds.contains(id) } }
    }
    private func layoutSmokeQuote(_ value: String) -> String { "'" + value.replacingOccurrences(of: "'", with: "'\\''") + "'" }
    private func layoutSmokePID(_ path: URL) -> pid_t? {
        guard let text = try? String(contentsOf: path, encoding: .utf8), let pid = pid_t(text.trimmingCharacters(in: .whitespacesAndNewlines)), pid > 1 else { return nil }
        return pid
    }
    private func layoutSmokeCommand(_ terminal: LocalTerminalSession, _ command: String) throws {
        guard terminal.view.paste(text: command), terminal.view.sendKey(.enter) else { throw MightyError("레이아웃 검증용 셸 입력에 실패했습니다.") }
    }

    private func layoutSmokeDropPoint(groupId: String, window: NSWindow, zone: PaneDockZone) throws -> NSPoint {
        window.contentView?.layoutSubtreeIfNeeded()
        guard let group = PaneDockDragCoordinator.shared.group(id: groupId, in: window) else { throw MightyError("드롭 대상의 실제 화면 영역이 없습니다.") }
        let bounds = group.paneDockVisibleRect
        let point: NSPoint
        switch zone {
        case .center: point = NSPoint(x: bounds.midX, y: bounds.midY)
        case .left: point = NSPoint(x: bounds.minX + 12, y: bounds.midY)
        case .right: point = NSPoint(x: bounds.maxX - 12, y: bounds.midY)
        case .top: point = NSPoint(x: bounds.midX, y: bounds.minY + 45)
        case .bottom: point = NSPoint(x: bounds.midX, y: bounds.maxY - 12)
        }
        return group.convert(point, to: nil)
    }

    /// Use the actual NSWindow hit test and normal application event dispatch.
    /// Synthetic events never move the user's cursor or read the clipboard.
    @discardableResult
    private func layoutSmokeNativeDrag(sessionId: String, window: NSWindow, destination: NSPoint, previewGroupId: String? = nil, previewFilename: String? = nil, cancel: Bool = false) throws -> Bool {
        let coordinator = PaneDockDragCoordinator.shared
        guard let content = window.contentView else { throw MightyError("검증할 창이 없습니다.") }
        window.makeKeyAndOrderFront(nil); content.layoutSubtreeIfNeeded()
        let inputState: [String: Any] = [
            "appIsActive": NSApp.isActive,
            "windowIsKey": window.isKeyWindow,
            "windowMatchesAppKeyWindow": NSApp.keyWindow === window,
            "windowIsOnActiveSpace": window.isOnActiveSpace,
            "frontmostIsOwnApplication": NSWorkspace.shared.frontmostApplication?.processIdentifier == ProcessInfo.processInfo.processIdentifier,
        ]
        let stateData = try JSONSerialization.data(withJSONObject: inputState, options: [.prettyPrinted, .sortedKeys])
        try stateData.write(to: dataDirectory.appendingPathComponent("layout-input-state.json"), options: .atomic)
        guard NSApp.isActive, window.isKeyWindow, NSApp.keyWindow === window, window.isOnActiveSpace else {
            throw MightyError("macOS가 검증 창을 활성화하지 않았습니다. 잠금 화면을 해제한 뒤 다시 검사하세요.")
        }
        guard let source = coordinator.tab(sessionId: sessionId, in: window) else { throw MightyError("네이티브 탭 드래그 핸들을 찾지 못했습니다.") }
        // Synthetic mouse events do not press the hardware button. Keep the
        // test gesture alive while a screenshot flushes AppKit work; production
        // still observes the real button to recover from a lost mouse-up.
        let originalEnvironment = source.gestureEnvironment
        source.gestureEnvironment.isLeftMouseButtonDown = { true }
        defer { source.cancelGesture(); source.gestureEnvironment = originalEnvironment }
        let sourceBounds = source.paneDockVisibleRect
        let start = source.convert(NSPoint(x: sourceBounds.midX, y: sourceBounds.midY), to: nil)
        let hit = content.hitTest(content.superview?.convert(start, from: nil) ?? start)
        guard hit === source else { throw MightyError("탭 이름의 실제 마우스 대상이 드래그 핸들이 아닙니다: \(hit.map { String(describing: type(of: $0)) } ?? "없음")") }
        func event(_ type: NSEvent.EventType, _ point: NSPoint, _ number: Int) throws -> NSEvent {
            guard let value = NSEvent.mouseEvent(with: type, location: point, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber, context: nil, eventNumber: number, clickCount: 1, pressure: type == .leftMouseUp ? 0 : 1) else { throw MightyError("마우스 검증 이벤트를 만들지 못했습니다.") }
            return value
        }
        let down = try event(.leftMouseDown, start, 1)
        let step = try event(.leftMouseDragged, NSPoint(x: start.x + 8, y: start.y), 2)
        let drag = try event(.leftMouseDragged, destination, 3)
        let up = try event(.leftMouseUp, destination, 4)
        let escape = cancel ? NSEvent.keyEvent(with: .keyDown, location: destination, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber, context: nil, characters: "\u{1b}", charactersIgnoringModifiers: "\u{1b}", isARepeat: false, keyCode: 53) : nil
        if cancel && escape == nil { throw MightyError("취소 검증 이벤트를 만들지 못했습니다.") }
        let beforeLayout = snapshot.paneLayouts
        let anchor = previewGroupId.flatMap { coordinator.group(id: $0, in: window) }
        let beforeDraw = anchor?.previewDrawCount ?? 0
        var previewError: Error?
        // Each handler must return to normal dispatch; a missing mouse-up must
        // never trap subsequent keyboard or application-activation events.
        NSApp.sendEvent(down)
        NSApp.sendEvent(step)
        NSApp.sendEvent(drag)
        let previewDrawn = anchor.map { $0.previewDrawCount > beforeDraw } == true && snapshot.paneLayouts == beforeLayout
        if let previewFilename, previewDrawn {
            do { _ = try captureSmokeWindow(window, filename: previewFilename) } catch { previewError = error }
        }
        let trace: [String: Any] = [
            "sessionId": sessionId, "start": NSStringFromPoint(start), "destination": NSStringFromPoint(destination),
            "sourceGroupId": source.groupId, "expectedGroupId": previewGroupId ?? "tab",
            "actualGroupId": coordinator.target?.groupId ?? "none", "zone": coordinator.target?.zone.rawValue ?? "none",
            "targetTab": coordinator.target?.tabId ?? "none",
            "expectedBounds": anchor.map { NSStringFromRect($0.bounds) } ?? "none",
            "expectedVisibleRect": anchor.map { NSStringFromRect($0.visibleRect) } ?? "none",
            "expectedClippedVisibleRect": anchor.map { NSStringFromRect($0.paneDockVisibleRect) } ?? "none",
            "expectedWindowRect": anchor.map { NSStringFromRect($0.convert($0.bounds, to: nil)) } ?? "none",
        ]
        if let data = try? JSONSerialization.data(withJSONObject: trace, options: [.prettyPrinted, .sortedKeys]) {
            try? data.write(to: dataDirectory.appendingPathComponent("layout-drag-\(coordinator.nativeDragStarts).json"))
        }
        if let escape { NSApp.sendEvent(escape) }
        NSApp.sendEvent(up)
        if let previewError { throw previewError }
        guard draggedPane == nil else { throw MightyError("드롭 후 드래그 상태가 정리되지 않았습니다.") }
        return previewDrawn
    }
}
