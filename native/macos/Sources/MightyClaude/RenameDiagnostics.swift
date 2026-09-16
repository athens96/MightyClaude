import AppKit
import MightyCore

extension AppStore {
    /// Uses only the isolated smoke profile and the native sheet field editor.
    func verifyRenameSmoke(window: NSWindow) async throws -> [String: Any] {
        guard ProcessInfo.processInfo.arguments.contains("--profile"),
              let workspace = activeWorkspace, let session = activeSessions.first else {
            throw MightyError("이름 변경 검증에는 격리된 워크스페이스와 실행 창이 필요합니다.")
        }
        let oldDraft = drafts[session.id]
        let oldAttachments = attachmentDrafts[session.id]
        defer {
            renameTarget = nil
            _ = renameWorkspace(workspace.id, to: workspace.name)
            _ = renameSession(session.id, to: session.title)
        }

        beginRenameSession(session.id)
        try await waitForSmoke(timeout: 2) { window.attachedSheet?.firstResponder is NSTextView }
        guard hasModal, let sheet = window.attachedSheet else { throw MightyError("이름 변경 창이 표시되지 않았습니다.") }
        // Native Return with a blank value must leave the sheet and session intact.
        try renameSmokeInput("  \n  ", sheet: sheet)
        try await Task.sleep(for: .milliseconds(50))
        try renameSmokeReturn(sheet: sheet)
        try await Task.sleep(for: .milliseconds(50))
        guard renameTarget?.targetID == session.id,
              snapshot.sessions.first(where: { $0.id == session.id }) == session else { throw MightyError("빈 이름이 저장되었습니다.") }
        try renameSmokeInput(String(repeating: "가", count: 121), sheet: sheet)
        try await Task.sleep(for: .milliseconds(50))
        try renameSmokeReturn(sheet: sheet)
        try await Task.sleep(for: .milliseconds(50))
        guard renameTarget != nil else { throw MightyError("길이 제한을 넘은 이름이 저장되었습니다.") }

        let sessionName = "👩‍💻 검증 · 실행 창 이름"
        try renameSmokeInput("  " + sessionName + "  ", sheet: sheet)
        try await Task.sleep(for: .milliseconds(50))
        let screenshot = try captureSmokeWindow(sheet, filename: "rename-session.png").path
        try renameSmokeReturn(sheet: sheet)
        try await waitForSmoke(timeout: 2) { self.renameTarget == nil && window.attachedSheet == nil }
        var renamedSession = session; renamedSession.title = sessionName
        guard snapshot.sessions.first(where: { $0.id == session.id }) == renamedSession,
              drafts[session.id] == oldDraft, attachmentDrafts[session.id] == oldAttachments else {
            throw MightyError("실행 창 이름 외의 상태가 변경되었습니다.")
        }

        beginRenameWorkspace(workspace.id)
        try await waitForSmoke(timeout: 2) { window.attachedSheet?.firstResponder is NSTextView }
        guard let workspaceSheet = window.attachedSheet else { throw MightyError("워크스페이스 이름 변경 창이 없습니다.") }
        let workspaceName = "이름 변경 검증"
        try renameSmokeInput(workspaceName, sheet: workspaceSheet)
        try await Task.sleep(for: .milliseconds(50))
        try renameSmokeReturn(sheet: workspaceSheet)
        try await waitForSmoke(timeout: 2) { self.renameTarget == nil && window.attachedSheet == nil }
        var renamedWorkspace = workspace; renamedWorkspace.name = workspaceName
        guard snapshot.workspaces.first(where: { $0.id == workspace.id }) == renamedWorkspace else {
            throw MightyError("워크스페이스 이름 외의 경로 또는 원격 정보가 변경되었습니다.")
        }
        try await flush()
        let restored = try await StateRepository(directory: dataDirectory, legacyStateURL: nil).load()
        guard restored.workspaces.first(where: { $0.id == workspace.id })?.name == workspaceName,
              restored.workspaces.first(where: { $0.id == workspace.id })?.path == workspace.path,
              restored.sessions.first(where: { $0.id == session.id })?.title == sessionName else {
            throw MightyError("변경한 이름이 저장 후 복원되지 않았습니다.")
        }
        return ["passed": true, "blankAndLongNameRejected": true, "nativeSheetReturnSaves": true,
                "sessionDataRetained": true, "workspacePathRetained": true, "namesPersist": true,
                "screenshot": screenshot]
    }

    private func renameSmokeInput(_ text: String, sheet: NSWindow) throws {
        guard let editor = sheet.firstResponder as? NSTextView, editor.isFieldEditor else { throw MightyError("이름 입력 필드에 포커스가 없습니다.") }
        editor.insertText(text, replacementRange: NSRange(location: 0, length: (editor.string as NSString).length))
    }

    private func renameSmokeReturn(sheet: NSWindow) throws {
        guard let editor = sheet.firstResponder as? NSTextView,
              let event = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: sheet.windowNumber, context: nil, characters: "\r", charactersIgnoringModifiers: "\r", isARepeat: false, keyCode: 36) else {
            throw MightyError("이름 저장 키 이벤트를 만들 수 없습니다.")
        }
        editor.keyDown(with: event)
    }
}
