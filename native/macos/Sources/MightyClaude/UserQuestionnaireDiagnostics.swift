import AppKit
import MightyCore
import SwiftUI

/// Local fixture only: clicking options never launches a CLI or submits an answer.
@MainActor
enum UserQuestionnaireDiagnostics {
    static func run(store: AppStore) async -> [String: Any] {
        var report: [String: Any] = ["passed": false, "realProviderRequests": 0]
        let sessionID = "question-presentation-fixture"
        let previousRequests = store.toolPermissions[sessionID]
        let previousError = store.permissionErrors[sessionID]
        let previousSnapshot = store.snapshot
        let previousRuntime = store.runtime
        let previousResponses = store.permissionResponses
        let previousWindow = NSApp.keyWindow
        let window = NSWindow(contentRect: NSRect(x: 160, y: 160, width: 680, height: 490),
                              styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        window.title = "선택 요청 표시 검증"
        window.isReleasedWhenClosed = false
        defer {
            store.toolPermissions[sessionID] = previousRequests
            store.permissionErrors[sessionID] = previousError
            store.permissionResponses = previousResponses
            store.snapshot = previousSnapshot
            store.runtime = previousRuntime
            window.orderOut(nil); window.close(); previousWindow?.makeKeyAndOrderFront(nil)
        }
        func check(_ name: String, _ value: Bool) throws {
            report[name] = value
            if !value { throw MightyError("선택 요청 화면 검사 실패: \(name)") }
        }
        func settle() async throws { try await Task.sleep(for: .milliseconds(140)) }
        do {
            let request = ToolPermissionRequest(id: "question-ui", runId: "fixture-run", toolUseId: "fixture-tool",
                toolName: "AskUserQuestion", inputJSON: fixtureJSON, summary: "선택 요청", canAllow: false, canAnswerQuestions: true)
            store.toolPermissions[sessionID] = [request]
            let host = NSHostingView(rootView: ToolPermissionBar(sessionId: sessionID).environmentObject(store)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top).background(Palette.panel))
            window.contentView = host
            window.makeKeyAndOrderFront(nil)
            try await settle()
            report["wideScreenshot"] = try store.captureSmokeWindow(window, filename: "questions-wide.png").path
            let accessibilityDeadline = Date().addingTimeInterval(3)
            while find(window, "questionnaire-next") == nil && Date() < accessibilityDeadline {
                try await Task.sleep(for: .milliseconds(100))
            }
            report["initialNextLocated"] = find(window, "questionnaire-next") != nil
            report["initialNextEnabledValue"] = enabled(window, "questionnaire-next") as Any? ?? NSNull()
            try check("initialNextDisabled", enabled(window, "questionnaire-next") == false)
            try check("cancelAvailable", enabled(window, "questionnaire-cancel") == true)
            try check("genericAllowButtonAbsent", find(window, "permission-allow-once") == nil)
            // One question at a time: the second one, 이전 and the submit action are not on screen yet.
            try check("onlyFirstQuestionShown", find(window, "questionnaire-question-0") != nil && find(window, "questionnaire-question-1") == nil)
            try check("firstStepHasNoBackOrSubmit", find(window, "questionnaire-back") == nil && find(window, "questionnaire-submit") == nil)
            try check("forwardDotBlockedUntilAnswered", enabled(window, "questionnaire-step-1") == false)

            try check("firstOptionAction", press(window, "questionnaire-option-0-0"))
            try await settle()
            try check("firstOptionSelected", value(window, "questionnaire-option-0-0") == "선택됨")
            try check("answerEnablesNext", enabled(window, "questionnaire-next") == true)
            try check("nextAction", press(window, "questionnaire-next"))
            try await settle()
            try check("secondQuestionReplacesFirst", find(window, "questionnaire-question-1") != nil && find(window, "questionnaire-question-0") == nil)
            try check("lastStepOffersBackAndSubmit", find(window, "questionnaire-back") != nil && find(window, "questionnaire-next") == nil)
            try check("partialAnswerDisabled", enabled(window, "questionnaire-submit") == false)
            try check("secondQuestionAction", press(window, "questionnaire-option-1-0"))
            try await settle()
            try check("allAnswersEnableSubmission", enabled(window, "questionnaire-submit") == true)
            try check("selectDoesNotSend", store.permissionResponses.isEmpty && store.toolPermissions[sessionID] == [request])

            try check("backAction", press(window, "questionnaire-back"))
            try await settle()
            try check("backKeepsEarlierPick", find(window, "questionnaire-question-0") != nil && value(window, "questionnaire-option-0-0") == "선택됨")
            try check("changeSingleOptionAction", press(window, "questionnaire-option-0-1"))
            try await settle()
            try check("singleSelectionReplacesPrevious", value(window, "questionnaire-option-0-0") == "선택 안 됨" && value(window, "questionnaire-option-0-1") == "선택됨")
            try check("customOptionAction", press(window, "questionnaire-custom-0"))
            try await settle()
            try check("customSelectionClearsSingleOption", value(window, "questionnaire-option-0-1") == "선택 안 됨")
            try check("blankCustomDisablesNext", enabled(window, "questionnaire-next") == false)
            try check("customEditorPresent", find(window, "questionnaire-custom-text-0") != nil)
            _ = press(window, "questionnaire-option-0-0")
            try await settle()
            _ = press(window, "questionnaire-next")
            try await settle()
            try check("forwardKeepsLaterPick", value(window, "questionnaire-option-1-0") == "선택됨" && enabled(window, "questionnaire-submit") == true)
            try check("dotJumpsBack", press(window, "questionnaire-step-0"))
            try await settle()
            try check("dotShowsFirstQuestion", find(window, "questionnaire-question-0") != nil && find(window, "questionnaire-next") != nil)
            _ = press(window, "questionnaire-step-1")
            try await settle()
            try check("dotJumpsForwardOverAnswered", find(window, "questionnaire-question-1") != nil && enabled(window, "questionnaire-submit") == true)
            window.setContentSize(NSSize(width: 360, height: 490))
            try await settle()
            report["narrowScreenshot"] = try store.captureSmokeWindow(window, filename: "questions-narrow.png").path
            let submitFrame = frame(window, "questionnaire-submit")
            let cancelFrame = frame(window, "questionnaire-cancel")
            try check("narrowActionsHaveRoom", submitFrame.width > 40 && cancelFrame.width > 20 && submitFrame.minX >= cancelFrame.maxX)

            let responseKey = store.permissionResponseKey(sessionId: sessionID, request: request)
            store.permissionResponses.insert(responseKey)
            try await settle()
            try check("busyDisablesSubmitAndCancel", enabled(window, "questionnaire-submit") == false && enabled(window, "questionnaire-cancel") == false)
            store.permissionResponses.remove(responseKey)

            var multi = request
            multi.id = "question-ui-multiple"
            multi.inputJSON = fixtureJSON.replacingOccurrences(of: "\"multiSelect\": false", with: "\"multiSelect\": true")
            store.toolPermissions[sessionID] = [multi]
            try await settle()
            _ = press(window, "questionnaire-option-0-0")
            _ = press(window, "questionnaire-option-0-1")
            try await settle()
            try check("multipleSelectionsRetained", value(window, "questionnaire-option-0-0") == "선택됨" && value(window, "questionnaire-option-0-1") == "선택됨")
            _ = press(window, "questionnaire-option-0-0")
            try await settle()
            try check("multipleSelectionTogglesOff", value(window, "questionnaire-option-0-0") == "선택 안 됨" && value(window, "questionnaire-option-0-1") == "선택됨")

            let transcript = AgentTranscriptFormat.entry(LogEntry(kind: "assistant", text: fixtureJSON), provider: "claude", running: false, expanded: false).string
            try check("transcriptContainsQuestionsAndDescriptions", transcript.contains("지금 보고 계신 화면이 어느 구현인가요?") && transcript.contains("타이틀바 safe area") && transcript.contains("어떤 방식으로 고칠까요?"))
            try check("transcriptHidesJSONSyntax", !transcript.contains("\"multiSelect\"") && !transcript.contains("\"questions\""))

            var ordinary = request
            ordinary.id = "ordinary-permission"; ordinary.toolName = "WebSearch"; ordinary.inputJSON = "{\"query\":\"Swift\"}"
            ordinary.canAllow = true; ordinary.canAnswerQuestions = false
            store.toolPermissions[sessionID] = [ordinary]
            try await settle()
            try check("ordinaryPermissionsPreserved", enabled(window, "permission-allow-once") == true && find(window, "questionnaire-submit") == nil)

            // Exercise the real pane at the minimum dock height. The question
            // viewport shrinks while its footer and the composer's stop action remain visible.
            let workspace = Workspace(id: "question-fixture-workspace", name: "선택 요청 검증", path: "/private/tmp")
            let session = RunSession(id: sessionID, workspaceId: workspace.id, title: "선택 요청", status: "running",
                                     logs: [LogEntry(kind: "assistant", text: "계속하기 전에 아래 질문에 답변해 주세요.")])
            store.snapshot = AppSnapshot(workspaces: [workspace], sessions: [session], activeWorkspaceId: workspace.id, activeSessionId: session.id)
            var provider = ProviderOptions.fallbackRuntime("claude")
            provider.available = true
            store.runtime = RuntimeInfo(claudeAvailable: true, providers: [provider])
            store.toolPermissions[sessionID] = [request]
            let shortHost = NSHostingView(rootView: SessionPaneView(session: session).environmentObject(store))
            // A standalone hosting window otherwise raises its minimum height
            // to the view's intrinsic size and silently evades the dock constraint.
            shortHost.sizingOptions = []
            window.contentView = shortHost
            window.setContentSize(NSSize(width: 360, height: 290))
            try await settle()
            report["shortPaneScreenshot"] = try store.captureSmokeWindow(window, filename: "questions-short-pane.png").path
            report["shortPaneActualWidth"] = shortHost.bounds.width
            report["shortPaneActualHeight"] = shortHost.bounds.height
            try check("shortPaneUsesRequestedSize", abs(shortHost.bounds.width - 360) < 1 && abs(shortHost.bounds.height - 290) < 1)
            // A fresh request opens on its first question, whose primary action is 다음.
            let shortSubmit = frame(window, "questionnaire-next")
            let shortCancel = frame(window, "questionnaire-cancel")
            let stop = frame(window, "composer-stop-" + sessionID)
            let questionViewport = frame(window, "questionnaire-viewport")
            report["shortPaneQuestionViewportHeight"] = questionViewport.height
            try check("shortPaneQuestionViewportUsable", questionViewport.height >= 48)
            let contentRect = window.contentRect(forFrameRect: window.frame)
            try check("shortPaneFooterAndStopVisible", [shortSubmit, shortCancel, stop].allSatisfy {
                $0.width > 0 && $0.height > 0 && contentRect.insetBy(dx: -1, dy: -1).contains($0)
            })
            try check("shortPaneFooterAboveComposer", shortSubmit.minY >= stop.maxY && shortCancel.minY >= stop.maxY)
            report["passed"] = true
        } catch {
            report["error"] = error.localizedDescription
            report["failureScreenshot"] = try? store.captureSmokeWindow(window, filename: "questions-failure.png").path
            report["accessibilityTree"] = accessibilityTree(window)
            if let view = window.contentView {
                report["hostingAccessibilityTree"] = accessibilityTree(view)
                report["nativeViewTree"] = nativeViewTree(view)
            }
        }
        return report
    }

    private static func find(_ element: Any, _ identifier: String, depth: Int = 0) -> NSObject? {
        guard depth < 50 else { return nil }
        let children: [Any]
        if let node = element as? any NSAccessibilityProtocol {
            if node.accessibilityIdentifier() == identifier { return node as? NSObject }
            children = node.accessibilityChildren() ?? []
        } else if let node = element as? NSObject {
            if node.responds(to: NSSelectorFromString("accessibilityIdentifier")), node.value(forKey: "accessibilityIdentifier") as? String == identifier { return node }
            children = node.responds(to: NSSelectorFromString("accessibilityChildren")) ? node.value(forKey: "accessibilityChildren") as? [Any] ?? [] : []
        } else { children = [] }
        for child in children { if let found = find(child, identifier, depth: depth + 1) { return found } }
        return nil
    }

    private static func accessibilityTree(_ element: Any, depth: Int = 0) -> [String: Any] {
        guard depth < 30, let object = element as? NSObject else { return ["truncated": true] }
        var row: [String: Any] = ["class": NSStringFromClass(type(of: object))]
        let selectorNames = ["accessibilityIdentifier", "isAccessibilityEnabled", "accessibilityValue", "accessibilityChildren", "accessibilityPerformPress", "accessibilityRole"]
        row["supportedSelectors"] = selectorNames.filter { object.responds(to: NSSelectorFromString($0)) }
        var children: [Any] = []
        if let node = element as? any NSAccessibilityProtocol {
            row["protocolConformance"] = true
            row["id"] = node.accessibilityIdentifier() ?? ""
            row["enabled"] = node.isAccessibilityEnabled()
            row["role"] = node.accessibilityRole()?.rawValue ?? ""
            row["value"] = node.accessibilityValue().map { String(describing: $0) } ?? ""
            children = node.accessibilityChildren() ?? []
        } else {
            row["protocolConformance"] = false
            for (selector, key) in [("accessibilityIdentifier", "id"), ("accessibilityValue", "value"), ("accessibilityRole", "role")] {
                if object.responds(to: NSSelectorFromString(selector)) {
                    row[key] = object.value(forKey: selector).map { String(describing: $0) } ?? ""
                }
            }
            if object.responds(to: NSSelectorFromString("isAccessibilityEnabled")) { row["enabled"] = object.value(forKey: "accessibilityEnabled") as Any? ?? NSNull() }
            if object.responds(to: NSSelectorFromString("accessibilityChildren")) { children = object.value(forKey: "accessibilityChildren") as? [Any] ?? [] }
        }
        row["children"] = children.prefix(100).map { accessibilityTree($0, depth: depth + 1) }
        return row
    }

    private static func nativeViewTree(_ view: NSView, depth: Int = 0) -> [String: Any] {
        var row: [String: Any] = ["class": NSStringFromClass(type(of: view)), "frame": NSStringFromRect(view.frame), "hidden": view.isHidden]
        if depth < 15 { row["children"] = view.subviews.prefix(100).map { nativeViewTree($0, depth: depth + 1) } }
        return row
    }
    private static func enabled(_ element: Any, _ id: String) -> Bool? {
        guard let node = find(element, id) else { return nil }
        if let accessible = node as? any NSAccessibilityProtocol { return accessible.isAccessibilityEnabled() }
        return node.responds(to: NSSelectorFromString("isAccessibilityEnabled")) ? node.value(forKey: "accessibilityEnabled") as? Bool : nil
    }
    private static func value(_ element: Any, _ id: String) -> String? {
        guard let node = find(element, id) else { return nil }
        if let accessible = node as? any NSAccessibilityProtocol { return accessible.accessibilityValue() as? String }
        return node.responds(to: NSSelectorFromString("accessibilityValue")) ? node.value(forKey: "accessibilityValue") as? String : nil
    }
    private static func frame(_ element: Any, _ id: String) -> NSRect {
        guard let node = find(element, id) else { return .zero }
        if let accessible = node as? any NSAccessibilityProtocol { return accessible.accessibilityFrame() }
        return node.responds(to: NSSelectorFromString("accessibilityFrame")) ? (node.value(forKey: "accessibilityFrame") as? NSValue)?.rectValue ?? .zero : .zero
    }
    private static func press(_ element: Any, _ id: String) -> Bool {
        guard let node = find(element, id) else { return false }
        if let accessible = node as? any NSAccessibilityProtocol { return accessible.accessibilityPerformPress() }
        let selector = NSSelectorFromString("accessibilityPerformPress")
        guard node.responds(to: selector), let implementation = node.method(for: selector) else { return false }
        typealias Press = @convention(c) (AnyObject, Selector) -> Bool
        return unsafeBitCast(implementation, to: Press.self)(node, selector)
    }

    static let fixtureJSON = #"""
    {"questions":[
      {"header":"대상 앱","multiSelect": false,"options":[
        {"description":"native/macos/ — 실제 배포 대상. .hiddenTitleBar의 타이틀바 safe area가 detail 컬럼 상단에 빈 띠로 남는 문제를 수정합니다.","label":"macOS 네이티브 앱 (Swift)"},
        {"description":"src/ + electron/ — .native-mac 헤더 높이 100px / padding-top 24px를 조정합니다.","label":"Electron 참조 구현 (npm run dev)"},
        {"description":"native/windows/ — 기본 WinUI 타이틀바 아래 root Grid의 Padding(12)과 브랜드 행 높이를 조정합니다.","label":"Windows 앱 (WinUI)"}],
        "question":"지금 보고 계신 화면이 어느 구현인가요?"},
      {"header":"수정 방향","multiSelect": false,"options":[
        {"description":"detail 컬럼이 타이틀바 영역까지 올라오고 워크스페이스 제목·경로가 그 자리를 차지합니다. 창 드래그는 이미 있는 WorkspaceTitlebarRegion이 계속 담당합니다. 사이드바는 신호등 공간을 유지.","label":"빈 띠를 헤더가 흡수 (권장)"},
        {"description":"빈 띠는 그대로 두고 제목 영역의 상하 패딩만 줄입니다. 변경 범위가 가장 작지만 빈 공간 자체는 남습니다.","label":"헤더 높이만 줄이기"}],
        "question":"어떤 방식으로 고칠까요?"}
    ]}
    """#
}
